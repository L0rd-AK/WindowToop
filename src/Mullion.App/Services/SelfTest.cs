using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Mullion.App.Interop;
using Mullion.App.UI;
using Mullion.Core;

namespace Mullion.App.Services;

/// <summary>
/// Exercises the real Win32 paths (styles, layering, topmost, hooks, hotkeys, DWM) against Mullion's own
/// windows, writes a PASS/FAIL report, and exits with code 0 or 1. Run with: Mullion.exe --selftest report.txt
/// </summary>
internal static class SelfTest
{
    private sealed class Report
    {
        public List<string> Lines { get; } = new();

        public int Failures { get; private set; }

        public void Check(string name, bool ok, string? detail = null)
        {
            Lines.Add($"{(ok ? "PASS" : "FAIL")} {name}{(string.IsNullOrEmpty(detail) ? string.Empty : " :: " + detail)}");
            if (!ok)
            {
                Failures++;
            }
        }

        public void Note(string text) => Lines.Add("     " + text);
    }

    public static async Task RunAsync(AppHost host, string reportPath)
    {
        var report = new Report();
        try
        {
            // The filter normally refuses our own windows; the self-test needs targets it fully controls.
            host.Controller.OwnProcessId = 0;

            // Target 1: a plain WinForms window (classic HWND, no WPF message handling). This is the
            // representative case for most desktop apps.
            using var form = new System.Windows.Forms.Form
            {
                Text = "Mullion self-test (WinForms)",
                Width = 400,
                Height = 200,
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            };
            form.Show();
            await Task.Delay(300);
            RawStyleProbe(report, form.Handle, "winforms", expectLayeredSupport: true);
            ControllerScenario(host, report, form.Handle, "winforms", expectLayeredSupport: true);
            form.Close();

            // Target 2: a WPF window. WPF strips WS_EX_LAYERED in WM_STYLECHANGING, so opacity and
            // click-through must be reported as skipped (not as success) and leave the style untouched.
            var window = new DebugWindow(host.Controller);
            var rendered = new TaskCompletionSource();
            window.ContentRendered += (_, _) => rendered.TrySetResult();
            window.Show();
            await Task.WhenAny(rendered.Task, Task.Delay(3000));

            // WPF finishes its initial activation/z-order work shortly after ContentRendered; a SetWindowPos
            // issued inside that window can be overridden. Let it settle, as a foreign target would have.
            await Task.Delay(750);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            nint hwnd = new WindowInteropHelper(window).Handle;
            report.Check("wpf: window created", hwnd != 0);
            RawStyleProbe(report, hwnd, "wpf", expectLayeredSupport: false);
            ControllerScenario(host, report, hwnd, "wpf", expectLayeredSupport: false);

            var probe = host.Controller.Probe(hwnd);
            report.Check("wpf: probe returns root", probe.Root == hwnd && probe.IsEligible, probe.Verdict.ToString());

            window.Close();
            await Task.Delay(100);
            report.Check("wpf: destroyed window is not a window", !host.Ops.IsWindow(hwnd));

            await Phase2Checks(host, report);
            await Phase3Checks(host, report);
            await Phase5Checks(host, report);
            await Phase4Checks(host, report); // last: it disposes the host
        }
        catch (Exception ex)
        {
            report.Check("no exception", false, ex.ToString());
        }

        report.Lines.Add(report.Failures == 0 ? "RESULT PASS" : $"RESULT FAIL ({report.Failures} failure(s))");
        try
        {
            File.WriteAllLines(reportPath, report.Lines);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Could not write self-test report: " + ex);
        }

        Application.Current.Shutdown(report.Failures == 0 ? 0 : 1);
    }

    /// <summary>Hotkeys (message-only window + WM_HOTKEY dispatch), tray icon, settings dialog, startup key.</summary>
    private static async Task Phase2Checks(AppHost host, Report report)
    {
        using (var hotkeys = new HotkeyService())
        {
            report.Check("hotkeys: message-only window created", hotkeys.Handle != 0);

            var failures = hotkeys.Register(new HotkeySettings());
            report.Check(
                "hotkeys: all five default bindings registered or explicitly refused",
                hotkeys.RegisteredCount + failures.Count == 5,
                $"registered={hotkeys.RegisteredCount} refused={string.Join(", ", failures.Select(f => f.Message))}");
            foreach (var failure in failures)
            {
                report.Note("hotkeys: refused (another program owns it): " + failure.Message);
            }

            // Fire a synthetic WM_HOTKEY for a binding that actually registered (another program may own some).
            var (id, expected) = hotkeys.Registered.First();
            HotkeyAction? received = null;
            hotkeys.Pressed += action => received = action;
            NativeMethods.SendMessage(hotkeys.Handle, NativeMethods.WM_HOTKEY, id, 0);
            report.Check("hotkeys: WM_HOTKEY dispatches to the right action", received == expected, $"sent id {id}, got {received}");

            // An id that was not registered must be ignored.
            received = null;
            NativeMethods.SendMessage(hotkeys.Handle, NativeMethods.WM_HOTKEY, 99, 0);
            report.Check("hotkeys: unknown WM_HOTKEY id is ignored", received is null);

            hotkeys.UnregisterAll();
            report.Check("hotkeys: unregister all", hotkeys.RegisteredCount == 0);
        }

        try
        {
            using var tray = new TrayService(host.Controller, () => host.Settings);
            report.Check("tray: icon created and drawn", true);
        }
        catch (Exception ex)
        {
            report.Check("tray: icon created and drawn", false, ex.Message);
        }

        var settings = new SettingsWindow(host.Settings);
        settings.Show();
        await Task.Delay(150);
        report.Check("settings: window loads with current bindings", settings.IsLoaded && settings.TopmostBox.Text == host.Settings.Hotkeys.ToggleTopmost, settings.TopmostBox.Text);
        settings.Close();

        bool startup = StartupService.IsEnabled();
        report.Note($"startup: Start-with-Windows currently {(startup ? "enabled" : "disabled")} (read-only check)");
    }

    /// <summary>WinEvent + mouse hooks, toolbar window styles, and the hover show/hide logic against a real window.</summary>
    private static async Task Phase3Checks(AppHost host, Report report)
    {
        // includeOwnProcess: the only windows we can destroy on demand live in this process.
        using var winEvents = new WinEventService(includeOwnProcess: true);
        report.Check("hooks: four WinEvent hooks installed", winEvents.HookCount == 4, winEvents.HookCount.ToString());

        using var mouse = new MouseHookService(Application.Current.Dispatcher);
        report.Check("hooks: WH_MOUSE_LL installed", mouse.IsInstalled);

        // A WinForms window as the "foreground" target; the coordinator asks us instead of GetForegroundWindow.
        using var form = new System.Windows.Forms.Form
        {
            Text = "Mullion self-test target",
            Width = 600,
            Height = 300,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Location = new System.Drawing.Point(120, 120),
        };
        form.Show();
        await Task.Delay(300);
        nint target = form.Handle;

        var settings = new AppSettings { ToolbarEnabled = true, ToolbarHideDelayMs = 150 };
        var reported = new List<OpResult>();
        using var coordinator = new ToolbarCoordinator(host.Controller, () => settings, () => target, reported.Add, Application.Current.Dispatcher);
        coordinator.Attach(winEvents, mouse);

        var frame = Win32WindowOps.GetFrameBounds(target);
        int strip = Win32WindowOps.GetCaptionHeight(target);
        report.Note($"toolbar: target frame={frame} caption strip={strip}px");

        // Pointer in the middle of the window body: nothing should appear.
        coordinator.OnMouseMoved(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2);
        report.Check("toolbar: stays hidden over the window body", !coordinator.IsToolbarVisible && coordinator.Target == target);

        // Pointer on the title bar: toolbar appears, centered on the top edge, never activating anything.
        coordinator.OnMouseMoved(frame.Left + frame.Width / 2, frame.Top + strip / 2);
        await Task.Delay(150);
        report.Check("toolbar: shows when hovering the title strip", coordinator.IsToolbarVisible);

        var toolbarHwnd = FindToolbarWindow();
        report.Check("toolbar: window exists", toolbarHwnd != 0);
        if (toolbarHwnd != 0)
        {
            uint ex = host.Ops.GetExStyle(toolbarHwnd);
            report.Check(
                "toolbar: WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST",
                (ex & WindowStyles.WS_EX_NOACTIVATE) != 0 && (ex & WindowStyles.WS_EX_TOOLWINDOW) != 0 && (ex & WindowStyles.WS_EX_TOPMOST) != 0,
                $"exstyle=0x{ex:X}");

            var rect = Win32WindowOps.GetWindowRect(toolbarHwnd);
            int expectedX = frame.Left + (frame.Width - rect.Width) / 2;
            report.Check(
                "toolbar: centered on the target's top edge",
                Math.Abs(rect.Left - expectedX) <= 2 && rect.Top >= frame.Top && rect.Top <= frame.Top + strip,
                $"toolbar={rect} target={frame}");
            report.Check("toolbar: did not take focus from the target", NativeMethods.GetForegroundWindow() != toolbarHwnd);

            // Pointer moves onto the toolbar itself: it must stay.
            coordinator.OnMouseMoved(rect.Left + 2, rect.Top + 2);
            await Task.Delay(settings.ToolbarHideDelayMs + 150);
            report.Check("toolbar: stays while the pointer is over it", coordinator.IsToolbarVisible);
        }

        // Pointer leaves both the strip and the toolbar: hides after the delay.
        coordinator.OnMouseMoved(frame.Right + 300, frame.Bottom + 300);
        await Task.Delay(settings.ToolbarHideDelayMs + 250);
        report.Check("toolbar: hides after the pointer leaves", !coordinator.IsToolbarVisible);

        // Disabled in settings: never shows.
        settings.ToolbarEnabled = false;
        coordinator.OnMouseMoved(frame.Left + frame.Width / 2, frame.Top + strip / 2);
        await Task.Delay(100);
        report.Check("toolbar: respects the toolbar-enabled setting", !coordinator.IsToolbarVisible);
        settings.ToolbarEnabled = true;

        // Destroying the target drops it from tracking and resets the coordinator.
        host.Controller.ToggleTopmost(target);
        report.Check("toolbar: target tracked before destroy", host.Controller.Find(target) is not null);
        form.Close();
        await Task.Delay(300);
        report.Check("hooks: EVENT_OBJECT_DESTROY untracked the closed window", host.Controller.Find(target) is null && coordinator.Target == 0, $"tracked={host.Controller.TrackedCount}");
    }

    /// <summary>Picture-in-picture: DWM thumbnail registration, frame styles, toggle semantics, source-destroyed cleanup.</summary>
    private static async Task Phase5Checks(AppHost host, Report report)
    {
        using var form = new System.Windows.Forms.Form
        {
            Text = "Mullion self-test PiP source",
            Width = 640,
            Height = 360,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
        };
        form.Show();
        await Task.Delay(300);
        nint source = form.Handle;

        var notes = new List<string>();
        using var pip = new PipManager(host.Controller, notes.Add);

        bool opened = pip.Toggle(source);
        await Task.Delay(300);
        report.Check("pip: toggle opens a frame", opened && pip.Count == 1 && pip.HasFrameFor(source), string.Join(" | ", notes));

        var frame = Application.Current.Windows.OfType<PipWindow>().FirstOrDefault(w => w.Source == source);
        report.Check("pip: frame window exists", frame is not null);
        if (frame is not null)
        {
            report.Check("pip: DwmRegisterThumbnail succeeded", frame.ThumbnailRegistered && frame.RegisterResult == 0, $"hr=0x{frame.RegisterResult:X8}");
            uint ex = host.Ops.GetExStyle(frame.Handle);
            report.Check(
                "pip: frame is topmost tool window and not layered (thumbnails need a non-layered target)",
                (ex & WindowStyles.WS_EX_TOPMOST) != 0 && (ex & WindowStyles.WS_EX_TOOLWINDOW) != 0 && (ex & WindowStyles.WS_EX_LAYERED) == 0,
                $"exstyle=0x{ex:X}");
            var rect = Win32WindowOps.GetWindowRect(frame.Handle);
            var work = Win32WindowOps.GetWorkArea(source);
            report.Check("pip: frame placed inside the work area", rect.Left >= work.Left && rect.Top >= work.Top && rect.Right <= work.Right && rect.Bottom <= work.Bottom, $"frame={rect} work={work}");
            report.Check("pip: thumbnail rectangle is non-empty and below the caption strip", !frame.ThumbnailRect.IsEmpty && frame.ThumbnailRect.Top >= 20, frame.ThumbnailRect.ToString());
            report.Check("pip: frame did not take focus from the source", NativeMethods.GetForegroundWindow() != frame.Handle);
        }

        bool stillOpen = pip.Toggle(source);
        await Task.Delay(150);
        report.Check("pip: toggle again closes the frame", !stillOpen && pip.Count == 0);

        pip.Toggle(source);
        await Task.Delay(150);
        pip.OnWindowDestroyed(source);
        await Task.Delay(150);
        report.Check("pip: destroyed source closes its frame", pip.Count == 0);

        report.Check("pip: refuses an ineligible handle", !pip.Toggle(0) && notes.Count > 0, notes.LastOrDefault());
        form.Close();
    }

    /// <summary>Persistence through the host, and restore-on-exit through the host's Dispose path.</summary>
    private static async Task Phase4Checks(AppHost host, Report report)
    {
        // Settings round trip through the store the host is using (the self-test runner points it at a temp file).
        var edited = host.Settings.Clone();
        edited.OpacityStep = 15;
        edited.ToolbarEnabled = !edited.ToolbarEnabled;
        edited.Hotkeys.Set(HotkeyAction.OpacityUp, "Ctrl+Shift+PageUp");
        host.SaveSettings(edited);
        var reloaded = new SettingsStore(host.SettingsStore.Path).Load();
        report.Check(
            "settings: saved through the host and reloaded from disk",
            File.Exists(host.SettingsStore.Path) && reloaded.OpacityStep == 15 && reloaded.ToolbarEnabled == edited.ToolbarEnabled && reloaded.Hotkeys.OpacityUp == "Ctrl+Shift+PageUp",
            host.SettingsStore.Path);

        // Restore-on-exit: modify a window, then go through the same Dispose every exit path uses.
        using var form = new System.Windows.Forms.Form
        {
            Text = "Mullion self-test exit target",
            Width = 400,
            Height = 200,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
        };
        form.Show();
        await Task.Delay(300);
        nint target = form.Handle;
        uint original = host.Ops.GetExStyle(target);

        host.Controller.ToggleTopmost(target);
        host.Controller.SetOpacity(target, 40);
        host.Controller.SetClickThrough(target, true);
        uint modified = host.Ops.GetExStyle(target);
        report.Check("exit: window modified before teardown", (modified & (WindowStyles.WS_EX_TOPMOST | WindowStyles.WS_EX_LAYERED | WindowStyles.WS_EX_TRANSPARENT)) == (WindowStyles.WS_EX_TOPMOST | WindowStyles.WS_EX_LAYERED | WindowStyles.WS_EX_TRANSPARENT), $"exstyle=0x{modified:X}");

        host.Dispose();
        uint after = host.Ops.GetExStyle(target);
        report.Check("exit: host.Dispose restored the window", after == original && host.Controller.TrackedCount == 0, $"before=0x{original:X} after=0x{after:X}");

        host.Dispose();
        report.Check("exit: Dispose is idempotent", true);
        form.Close();
    }

    private static nint FindToolbarWindow()
    {
        foreach (var window in Application.Current.Windows)
        {
            if (window is ToolbarWindow toolbar)
            {
                return toolbar.Handle;
            }
        }

        return 0;
    }

    /// <summary>Calls NativeMethods directly and logs raw values so a failure can be attributed to Win32 vs. the controller.</summary>
    private static void RawStyleProbe(Report report, nint hwnd, string label, bool expectLayeredSupport)
    {
        nint before = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        report.Note($"{label}: raw exstyle before = 0x{before:X}");

        bool ok = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        int err = Marshal.GetLastWin32Error();
        nint afterTop = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        report.Note($"{label}: raw SetWindowPos(HWND_TOPMOST) attempt 1 ok={ok} err={err} exstyle=0x{afterTop:X}");
        if ((afterTop & (nint)WindowStyles.WS_EX_TOPMOST) == 0)
        {
            // Seen on WPF windows: the first HWND_TOPMOST request reports success but the band does not change.
            // Flipping to HWND_NOTOPMOST and back makes it take; Win32WindowOps.SetTopmost does the same.
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            ok = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            err = Marshal.GetLastWin32Error();
            afterTop = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            report.Note($"{label}: raw retry (NOTOPMOST then TOPMOST) ok={ok} err={err} exstyle=0x{afterTop:X}");
        }

        report.Check($"{label}: raw SetWindowPos(HWND_TOPMOST) sets WS_EX_TOPMOST", ok && (afterTop & (nint)WindowStyles.WS_EX_TOPMOST) != 0, $"ok={ok} err={err} exstyle=0x{afterTop:X}");

        Marshal.SetLastSystemError(0);
        nint prev = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, afterTop | (nint)WindowStyles.WS_EX_LAYERED);
        err = Marshal.GetLastWin32Error();
        nint afterLayered = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        bool layeredStuck = (afterLayered & (nint)WindowStyles.WS_EX_LAYERED) != 0;
        report.Check(
            expectLayeredSupport ? $"{label}: raw SetWindowLongPtr adds WS_EX_LAYERED" : $"{label}: raw SetWindowLongPtr WS_EX_LAYERED is stripped by WPF (expected)",
            layeredStuck == expectLayeredSupport,
            $"prev=0x{prev:X} err={err} exstyle=0x{afterLayered:X}");

        if (expectLayeredSupport)
        {
            ok = NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 128, NativeMethods.LWA_ALPHA);
            err = Marshal.GetLastWin32Error();
            byte? alpha = Win32WindowOps.TryGetLayeredAlpha(hwnd);
            report.Check($"{label}: raw SetLayeredWindowAttributes(128)", ok && alpha == 128, $"ok={ok} err={err} alpha={alpha}");
        }

        // Put it back exactly as found.
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, before);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        nint restored = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        report.Check($"{label}: raw restore", restored == before, $"exstyle=0x{restored:X}");
    }

    private static void ControllerScenario(AppHost host, Report report, nint hwnd, string label, bool expectLayeredSupport)
    {
        var controller = host.Controller;
        var ops = host.Ops;
        uint original = ops.GetExStyle(hwnd);

        var r = controller.ToggleTopmost(hwnd);
        report.Check($"{label}: topmost on", r.Succeeded && Has(ops, hwnd, WindowStyles.WS_EX_TOPMOST), $"{r.Message} exstyle=0x{ops.GetExStyle(hwnd):X}");

        if (expectLayeredSupport)
        {
            r = controller.SetOpacity(hwnd, 50);
            byte? alpha = Win32WindowOps.TryGetLayeredAlpha(hwnd);
            report.Check($"{label}: opacity 50% adds WS_EX_LAYERED + alpha 128", r.Succeeded && Has(ops, hwnd, WindowStyles.WS_EX_LAYERED) && alpha == 128, $"{r.Message} alpha={alpha} exstyle=0x{ops.GetExStyle(hwnd):X}");

            r = controller.ToggleClickThrough(hwnd);
            report.Check($"{label}: click-through on sets WS_EX_TRANSPARENT", r.Succeeded && Has(ops, hwnd, WindowStyles.WS_EX_TRANSPARENT), $"{r.Message} exstyle=0x{ops.GetExStyle(hwnd):X}");

            r = controller.SetOpacity(hwnd, 100);
            alpha = Win32WindowOps.TryGetLayeredAlpha(hwnd);
            report.Check($"{label}: opacity 100% keeps layered while click-through", r.Succeeded && Has(ops, hwnd, WindowStyles.WS_EX_LAYERED) && alpha == 255, $"{r.Message} alpha={alpha}");

            r = controller.ToggleClickThrough(hwnd);
            report.Check($"{label}: click-through off drops layered + transparent", r.Succeeded && !Has(ops, hwnd, WindowStyles.WS_EX_LAYERED) && !Has(ops, hwnd, WindowStyles.WS_EX_TRANSPARENT), $"{r.Message} exstyle=0x{ops.GetExStyle(hwnd):X}");
        }
        else
        {
            uint pinned = ops.GetExStyle(hwnd);

            r = controller.SetOpacity(hwnd, 50);
            report.Check($"{label}: opacity reported as skipped, style untouched", r.Status == OpStatus.Skipped && ops.GetExStyle(hwnd) == pinned, $"{r.Status}: {r.Message} exstyle=0x{ops.GetExStyle(hwnd):X}");

            r = controller.ToggleClickThrough(hwnd);
            report.Check($"{label}: click-through reported as skipped, no stray WS_EX_TRANSPARENT", r.Status == OpStatus.Skipped && ops.GetExStyle(hwnd) == pinned, $"{r.Status}: {r.Message} exstyle=0x{ops.GetExStyle(hwnd):X}");
        }

        report.Check($"{label}: still tracked (pinned)", controller.TrackedCount == 1 && controller.Find(hwnd)?.IsTopmost == true);

        int restored = controller.RestoreAll();
        uint after = ops.GetExStyle(hwnd);
        report.Check($"{label}: restore all", restored == 1 && after == original && controller.TrackedCount == 0, $"restored={restored} before=0x{original:X8} after=0x{after:X8}");
    }

    private static bool Has(IWindowOps ops, nint hwnd, uint bit) => (ops.GetExStyle(hwnd) & bit) != 0;
}

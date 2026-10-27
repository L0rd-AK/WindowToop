using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using Mullion.App.Interop;
using Mullion.Core;

namespace Mullion.App.Services;

/// <summary>
/// End-to-end check for "PiP freezes when I switch to another program" (run with --pipcheck report.txt).
///
/// Opens an animated page in a throwaway profile of a Chromium-based browser, shows it in PiP, then covers
/// the browser with an opaque window. The page reports its own requestAnimationFrame counter and
/// document.visibilityState in its title, which tells whether the browser kept rendering; screen pixels inside
/// the PiP view tell whether the user would actually see it moving. Runs once without keep-awake (to confirm
/// the browser throttles) and once with each candidate alpha.
/// </summary>
internal static class PipCheck
{
    private const string TitlePrefix = "MULLIONPIP";

    private const string Page = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>MULLIONPIP|init|0</title>
        <style>html,body{margin:0;height:100%;overflow:hidden}</style></head>
        <body><script>
        let n = 0;
        function frame() {
          n++;
          const hue = (n * 7) % 360;
          document.body.style.background = `hsl(${hue}, 90%, 60%)`;
          document.title = `MULLIONPIP|${document.visibilityState}|${n}`;
          requestAnimationFrame(frame);
        }
        requestAnimationFrame(frame);
        </script></body></html>
        """;

    private sealed record Sample(string Visibility, long Frames, Color Pixel);

    public static async Task RunAsync(AppHost host, string reportPath, string? browserOverride)
    {
        var lines = new List<string>();
        int failures = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            lines.Add($"{(ok ? "PASS" : "FAIL")} {name}{(string.IsNullOrEmpty(detail) ? string.Empty : " :: " + detail)}");
            if (!ok)
            {
                failures++;
            }
        }

        void Note(string text) => lines.Add("     " + text);

        Process? browser = null;
        string workDir = Path.Combine(Path.GetTempPath(), "mullion-pipcheck");
        try
        {
            string? exe = browserOverride ?? FindBrowser();
            if (exe is null || !File.Exists(exe))
            {
                Check("browser found", false, "pass --browser <path to msedge.exe or chrome.exe>");
                return;
            }

            Note("browser: " + exe);
            Directory.CreateDirectory(workDir);
            string page = Path.Combine(workDir, "pip.html");
            File.WriteAllText(page, Page);

            browser = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                ArgumentList =
                {
                    "--user-data-dir=" + Path.Combine(workDir, "profile-" + Path.GetFileNameWithoutExtension(exe)),
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--disable-sync",
                    "--window-position=120,120",
                    "--window-size=800,520",
                    "--app=" + new Uri(page).AbsoluteUri,
                },
            });

            nint source = await WaitForWindowAsync(TimeSpan.FromSeconds(20));
            Check("browser window found", source != 0);
            if (source == 0)
            {
                return;
            }

            // Wait until the page script is actually animating (a reused profile can take a while to load the page).
            var started = DateTime.UtcNow;
            while (Read(source, frame: null).Frames <= 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(250);
            }

            // A browser launched from a background process may open behind other windows. Raise it (no activation
            // needed for z-order) and confirm by hit-testing its centre before taking the baseline.
            NativeMethods.SetWindowPos(source, 0 /* HWND_TOP */, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            await Task.Delay(1000);
            var rect = Win32WindowOps.GetWindowRect(source);
            nint hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = rect.Left + rect.Width / 2, Y = rect.Top + rect.Height / 2 });
            bool onTop = host.Ops.GetRootWindow(hit) == source;
            Note($"baseline: browser window on top at its centre = {onTop} (hit {Describe(host.Ops.GetRootWindow(hit))})");
            foreach (string occluder in Occluders(source))
            {
                Note("baseline: above and overlapping the browser: " + occluder);
            }

            var baseline = await MeasureAsync(source, frame: null);
            Check("page renders while visible", baseline.Live || !onTop, baseline.Live ? baseline.Detail : $"{baseline.Detail} (window not on top; baseline inconclusive)");

            // Run without keep-awake first: confirms the browser really throttles when covered on this machine.
            var off = await RunScenarioAsync(host, source, keepAwake: false, alpha: 0, Note);
            Check("without keep-awake: covering the browser pauses rendering (the reported bug)", !off.PageLive, off.Detail);

            foreach (byte alpha in new byte[] { 1, 0 })
            {
                var on = await RunScenarioAsync(host, source, keepAwake: true, alpha, Note);
                bool chosen = alpha == PipKeepAlive.DefaultGhostAlpha;
                string label = $"keep-awake alpha {alpha}{(chosen ? " (default)" : " (alternative)")}";
                if (chosen)
                {
                    Check($"{label}: page keeps rendering while covered", on.PageLive, on.Detail);
                    Check($"{label}: PiP view shows the content (not blank)", on.ThumbnailVisible, on.Detail);
                    Check($"{label}: PiP view keeps changing", on.ThumbnailMoving, on.Detail);
                    Check($"{label}: source restored after close", on.Restored, on.Detail);
                }
                else
                {
                    Note($"{label}: pageLive={on.PageLive} thumbnailVisible={on.ThumbnailVisible} moving={on.ThumbnailMoving} restored={on.Restored} :: {on.Detail}");
                }
            }
        }
        catch (Exception ex)
        {
            Check("no exception", false, ex.ToString());
        }
        finally
        {
            try
            {
                if (browser is { HasExited: false })
                {
                    browser.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Note("could not stop browser: " + ex.Message);
            }

            lines.Add(failures == 0 ? "RESULT PASS" : $"RESULT FAIL ({failures} failure(s))");
            try
            {
                File.WriteAllLines(reportPath, lines);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Could not write pipcheck report: " + ex);
            }

            Application.Current.Shutdown(failures == 0 ? 0 : 1);
        }
    }

    private sealed record ScenarioResult(bool PageLive, bool ThumbnailVisible, bool ThumbnailMoving, bool Restored, string Detail);

    private static async Task<ScenarioResult> RunScenarioAsync(AppHost host, nint source, bool keepAwake, byte alpha, Action<string> note)
    {
        uint originalStyle = host.Ops.GetExStyle(source);
        var notes = new List<string>();
        using var pip = new PipManager(host.Controller, host.Ops, () => keepAwake, notes.Add) { GhostAlpha = alpha };
        pip.Toggle(source);
        var frame = pip.FrameFor(source);
        if (frame is null)
        {
            return new ScenarioResult(false, false, false, false, "PiP did not open: " + string.Join(" | ", notes));
        }

        // An opaque, ordinary window over the browser: what "switching to another program" does.
        var bounds = Win32WindowOps.GetWindowRect(source);
        using var cover = new System.Windows.Forms.Form
        {
            Text = "Mullion pipcheck cover",
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            BackColor = Color.FromArgb(40, 40, 40),
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            ShowInTaskbar = false,
            Bounds = new Rectangle(bounds.Left - 40, bounds.Top - 40, bounds.Width + 80, bounds.Height + 80),
        };
        cover.Show();
        NativeMethods.SetWindowPos(cover.Handle, 0 /* HWND_TOP */, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
        cover.Activate();

        // Measure the view away from the bottom-right corner, where notification toasts can sit on top of it.
        var work = Win32WindowOps.GetWorkArea(source);
        var frameRect = Win32WindowOps.GetWindowRect(frame.Handle);
        NativeMethods.SetWindowPos(frame.Handle, NativeMethods.HWND_TOPMOST, work.Left + 24, work.Top + 24, frameRect.Width, frameRect.Height, NativeMethods.SWP_NOACTIVATE);

        // Chromium recomputes occlusion asynchronously after window changes; give it time to react.
        await Task.Delay(2500);
        foreach (string occluder in Occluders(source))
        {
            note($"scenario keepAwake={keepAwake} alpha={alpha}: above and overlapping the browser: {occluder}");
        }

        var measured = await MeasureAsync(source, frame);

        cover.Close();
        pip.Toggle(source); // close the view, which restores the source
        await Task.Delay(400);
        uint afterStyle = host.Ops.GetExStyle(source);
        bool restored = afterStyle == originalStyle && !host.Controller.IsSuspended(source);

        string detail = $"{measured.Detail} restoredStyle=0x{afterStyle:X}/0x{originalStyle:X}{(notes.Count > 0 ? " notes=" + string.Join(" | ", notes) : string.Empty)}";
        note($"scenario keepAwake={keepAwake} alpha={alpha}: {detail}");

        // Let the page settle back to visible before the next scenario.
        await Task.Delay(1000);
        return new ScenarioResult(measured.Live, measured.ThumbnailVisible, measured.ThumbnailMoving, restored, detail);
    }

    private sealed record Measurement(bool Live, bool ThumbnailVisible, bool ThumbnailMoving, string Detail);

    private static async Task<Measurement> MeasureAsync(nint source, UI.PipWindow? frame)
    {
        var a = Read(source, frame);
        await Task.Delay(1200);
        var b = Read(source, frame);

        long frames = b.Frames - a.Frames;
        bool live = frames > 10 && b.Visibility == "visible";

        bool visible = false;
        bool moving = false;
        if (frame is not null)
        {
            // The page is a saturated colour at 60% lightness; the empty PiP background is near-black.
            visible = Brightness(a.Pixel) > 80 && Brightness(b.Pixel) > 80;
            moving = ColorDistance(a.Pixel, b.Pixel) > 20;
        }

        string detail = $"frames+{frames} visibility={b.Visibility} pixel {Hex(a.Pixel)}->{Hex(b.Pixel)}";
        return new Measurement(live, visible, moving, detail);
    }

    private static Sample Read(nint source, UI.PipWindow? frame)
    {
        string title = Win32WindowOps.GetWindowText(source);
        string[] parts = title.Split('|');
        string visibility = parts.Length >= 3 ? parts[1] : "?";
        long frames = parts.Length >= 3 && long.TryParse(parts[2], out long n) ? n : -1;

        Color pixel = Color.Empty;
        if (frame is not null)
        {
            var r = frame.ScreenThumbnailRect;
            if (!r.IsEmpty)
            {
                pixel = AverageColor(r.Left + r.Width / 2 - 8, r.Top + r.Height / 2 - 8, 16, 16);
            }
        }

        return new Sample(visibility, frames, pixel);
    }

    private static Color AverageColor(int x, int y, int width, int height)
    {
        try
        {
            using var bitmap = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
            }

            long r = 0, gg = 0, b = 0;
            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < height; j++)
                {
                    var c = bitmap.GetPixel(i, j);
                    r += c.R;
                    gg += c.G;
                    b += c.B;
                }
            }

            int count = width * height;
            return Color.FromArgb((int)(r / count), (int)(gg / count), (int)(b / count));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            // Locked workstation or secure desktop: screen capture is unavailable.
            return Color.Empty;
        }
    }

    /// <summary>
    /// Windows above <paramref name="source"/> in z-order that overlap it and that Chromium's occlusion tracker
    /// counts as opaque: visible, not minimized, not cloaked, not tool windows, not click-through, and not layered
    /// with alpha below 255.
    /// </summary>
    private static IEnumerable<string> Occluders(nint source)
    {
        var target = Win32WindowOps.GetWindowRect(source);
        foreach (nint hwnd in Win32WindowOps.EnumerateTopLevelWindows())
        {
            if (hwnd == source)
            {
                yield break;
            }

            if (!NativeMethods.IsWindowVisible(hwnd) || Win32WindowOps.IsMinimized(hwnd) || Win32WindowOps.IsCloaked(hwnd))
            {
                continue;
            }

            uint ex = unchecked((uint)(long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE));
            if ((ex & (WindowStyles.WS_EX_TOOLWINDOW | WindowStyles.WS_EX_TRANSPARENT)) != 0)
            {
                continue;
            }

            if ((ex & WindowStyles.WS_EX_LAYERED) != 0 && Win32WindowOps.TryGetLayeredAlpha(hwnd) is byte a && a < 255)
            {
                continue;
            }

            var r = Win32WindowOps.GetWindowRect(hwnd);
            bool overlaps = r.Left < target.Right && r.Right > target.Left && r.Top < target.Bottom && r.Bottom > target.Top;
            if (overlaps)
            {
                yield return Describe(hwnd);
            }
        }
    }

    private static string Describe(nint hwnd)
    {
        if (hwnd == 0)
        {
            return "(none)";
        }

        uint ex = unchecked((uint)(long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE));
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string process;
        try
        {
            process = Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
            process = "?";
        }

        string title = Win32WindowOps.GetWindowText(hwnd);
        if (title.Length > 40)
        {
            title = title[..40];
        }

        return $"[{process}] class={Win32WindowOps.GetClassName(hwnd)} title=\"{title}\" ex=0x{ex:X} rect={Win32WindowOps.GetWindowRect(hwnd)}";
    }

    private static double Brightness(Color c) => (c.R + c.G + c.B) / 3.0;

    private static double ColorDistance(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    private static string Hex(Color c) => c.IsEmpty ? "n/a" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static async Task<nint> WaitForWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (nint hwnd in Win32WindowOps.EnumerateTopLevelWindows())
            {
                if (NativeMethods.IsWindowVisible(hwnd) && Win32WindowOps.GetWindowText(hwnd).StartsWith(TitlePrefix + "|", StringComparison.Ordinal))
                {
                    return hwnd;
                }
            }

            await Task.Delay(250);
        }

        return 0;
    }

    private static string? FindBrowser()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

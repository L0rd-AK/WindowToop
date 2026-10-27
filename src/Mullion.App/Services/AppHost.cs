using System.IO;
using System.Windows;
using System.Windows.Threading;
using Mullion.App.Interop;
using Mullion.App.UI;
using Mullion.Core;

namespace Mullion.App.Services;

/// <summary>
/// Composition root: creates the controller and services, starts them, and tears everything down
/// in the right order. <see cref="Dispose"/> is idempotent so every exit path can call it.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private readonly LaunchOptions _options;
    private HotkeyService? _hotkeys;
    private TrayService? _tray;
    private WinEventService? _winEvents;
    private MouseHookService? _mouse;
    private ToolbarCoordinator? _toolbar;
    private PipManager? _pip;
    private DebugWindow? _debugWindow;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _smokeTimer;
    private bool _disposed;

    public AppHost(LaunchOptions options)
    {
        _options = options;
        Ops = new Win32WindowOps();
        Controller = new WindowController(Ops, (uint)Environment.ProcessId);
        SettingsStore = new SettingsStore(options.SettingsPath ?? SettingsStore.DefaultPath);
        Settings = SettingsStore.Load();
        if (SettingsStore.LastLoadError is { } loadError)
        {
            Diagnostics.Log($"Settings file could not be read, using defaults: {loadError}");
        }
    }

    public Win32WindowOps Ops { get; }

    public WindowController Controller { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; private set; }

    /// <summary>Opens or closes a picture-in-picture frame for a window; null until the service is started.</summary>
    public Action<nint>? PipHandler { get; private set; }

    /// <summary>Raised after settings were saved.</summary>
    public event Action<AppSettings>? SettingsChanged;

    public void Start()
    {
        if (_options.SelfTest)
        {
            _ = SelfTest.RunAsync(this, _options.SelfTestReportPath!);
            return;
        }

        _pip = new PipManager(Controller, message => Notify("Picture-in-picture", message));
        PipHandler = hwnd => _pip.Toggle(hwnd);

        _tray = new TrayService(Controller, () => Settings) { PipAvailable = true };
        _tray.ExitRequested += () => Application.Current.Shutdown();
        _tray.RestoreAllRequested += () => Controller.RestoreAll();
        _tray.SettingsRequested += ShowSettings;
        _tray.DebugConsoleRequested += ShowDebugWindow;
        _tray.ToolbarEnabledChanged += enabled =>
        {
            var updated = Settings.Clone();
            updated.ToolbarEnabled = enabled;
            SaveSettings(updated);
        };
        _tray.PipRequested += hwnd => PipHandler?.Invoke(hwnd);

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys();

        _winEvents = new WinEventService();
        _winEvents.WindowDestroyed += _pip.OnWindowDestroyed;
        _mouse = new MouseHookService(Application.Current.Dispatcher);
        _toolbar = new ToolbarCoordinator(Controller, () => Settings, NativeMethods.GetForegroundWindow, Report, Application.Current.Dispatcher)
        {
            PipHandler = PipHandler,
        };
        _toolbar.Attach(_winEvents, _mouse);

        Diagnostics.Log($"Started: hotkeys={_hotkeys.RegisteredCount}, winEventHooks={_winEvents.HookCount}, mouseHook={_mouse.IsInstalled}, toolbar={Settings.ToolbarEnabled}, settings={SettingsStore.Path}");

        if (_options.ShowDebugWindow)
        {
            ShowDebugWindow();
        }

        if (_options.SmokeDurationMs > 0)
        {
            _smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_options.SmokeDurationMs) };
            _smokeTimer.Tick += (_, _) =>
            {
                _smokeTimer.Stop();
                Diagnostics.Log("Smoke run complete; shutting down.");
                Application.Current.Shutdown();
            };
            _smokeTimer.Start();
        }
    }

    public void ShowDebugWindow()
    {
        if (_debugWindow is null)
        {
            _debugWindow = new DebugWindow(Controller, PipHandler);
            _debugWindow.Closed += (_, _) => _debugWindow = null;
        }

        _debugWindow.Show();
        _debugWindow.Activate();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(Settings);
        _settingsWindow.Closed += (_, _) =>
        {
            bool saved = _settingsWindow.DialogResult == true;
            var result = _settingsWindow.Result;
            _settingsWindow = null;
            if (saved)
            {
                SaveSettings(result);
                ApplyHotkeys();
            }
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        try
        {
            SettingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("Could not save settings: " + ex.Message);
            Notify("Settings", "Settings could not be saved: " + ex.Message, NotifyLevel.Error);
        }

        if (!settings.ToolbarEnabled)
        {
            _toolbar?.HideNow();
        }

        SettingsChanged?.Invoke(settings);
    }

    public void Notify(string title, string text, NotifyLevel level = NotifyLevel.Info) => _tray?.Notify(title, text, level);

    /// <summary>Surfaces an operation result to the user: silent on plain success, a balloon for anything they need to know.</summary>
    public void Report(OpResult result)
    {
        switch (result.Status)
        {
            case OpStatus.Ok when result.Warning is not null:
                Notify("Mullion", result.Warning, NotifyLevel.Warning);
                break;
            case OpStatus.Ok when result.Window?.ClickThrough == true && result.Message.StartsWith("Click-through on", StringComparison.Ordinal):
                Notify(
                    "Click-through on",
                    $"\"{result.Window.Title}\" now ignores the mouse. Press {Settings.Hotkeys.ToggleClickThrough} while it has focus, hover its title bar for the toolbar, or use the tray menu to turn it off.");
                break;
            case OpStatus.Ok:
                break;
            case OpStatus.AccessDenied:
                Notify("Can't modify this window", result.Message, NotifyLevel.Warning);
                break;
            case OpStatus.NotEligible:
            case OpStatus.Skipped:
                Notify("Mullion", result.Message);
                break;
            case OpStatus.Failed:
                Notify("Mullion", result.Message, NotifyLevel.Error);
                break;
        }
    }

    private void ApplyHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }

        var failures = _hotkeys.Register(Settings.Hotkeys);
        if (failures.Count > 0)
        {
            Notify("Some hotkeys are unavailable", string.Join("\n", failures.Select(f => f.Message)), NotifyLevel.Warning);
            Diagnostics.Log("Hotkey registration failures: " + string.Join("; ", failures.Select(f => f.Message)));
        }
    }

    private void OnHotkey(HotkeyAction action)
    {
        nint hwnd = NativeMethods.GetForegroundWindow();
        switch (action)
        {
            case HotkeyAction.ToggleTopmost:
                Report(Controller.ToggleTopmost(hwnd));
                break;
            case HotkeyAction.OpacityUp:
                Report(Controller.AdjustOpacity(hwnd, Settings.OpacityStep));
                break;
            case HotkeyAction.OpacityDown:
                Report(Controller.AdjustOpacity(hwnd, -Settings.OpacityStep));
                break;
            case HotkeyAction.ToggleClickThrough:
                Report(Controller.ToggleClickThrough(hwnd));
                break;
            case HotkeyAction.PictureInPicture:
                PipHandler?.Invoke(hwnd);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _smokeTimer?.Stop();

        // Our own windows and hooks first, then hotkeys and tray, and foreign windows last so nothing can interfere.
        SafeDispose(() => _pip?.Dispose(), nameof(PipManager));
        SafeDispose(() => _toolbar?.Dispose(), nameof(ToolbarCoordinator));
        SafeDispose(() => _mouse?.Dispose(), nameof(MouseHookService));
        SafeDispose(() => _winEvents?.Dispose(), nameof(WinEventService));
        SafeDispose(() => _hotkeys?.Dispose(), nameof(HotkeyService));
        SafeDispose(() => _tray?.Dispose(), nameof(TrayService));

        try
        {
            int restored = Controller.RestoreAll();
            if (restored > 0)
            {
                Diagnostics.Log($"Restored {restored} window(s) on exit.");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("RestoreAll failed: " + ex);
        }
    }

    private static void SafeDispose(Action dispose, string what)
    {
        try
        {
            dispose();
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"{what} teardown failed: {ex}");
        }
    }
}

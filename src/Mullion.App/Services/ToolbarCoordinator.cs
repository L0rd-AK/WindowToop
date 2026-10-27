using System.Windows.Threading;
using Mullion.App.Interop;
using Mullion.App.UI;
using Mullion.Core;

namespace Mullion.App.Services;

/// <summary>
/// Decides when and where the floating toolbar appears: tracks the foreground window, watches the pointer,
/// and hides the toolbar when the pointer leaves the title strip and the toolbar itself.
/// All members run on the UI thread.
/// </summary>
internal sealed class ToolbarCoordinator : IDisposable
{
    /// <summary>How far below the window's top edge the toolbar sits, in DIPs (scaled per monitor).</summary>
    private const int TopInsetDip = 6;

    private readonly WindowController _controller;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<nint> _getForegroundWindow;
    private readonly Action<OpResult> _report;
    private readonly DispatcherTimer _hideTimer;
    private ToolbarWindow? _toolbar;
    private nint _target;
    private PixelRect _targetRect;
    private int _stripHeight;
    private bool _disposed;

    public ToolbarCoordinator(
        WindowController controller,
        Func<AppSettings> getSettings,
        Func<nint> getForegroundWindow,
        Action<OpResult> report,
        Dispatcher dispatcher)
    {
        _controller = controller;
        _getSettings = getSettings;
        _getForegroundWindow = getForegroundWindow;
        _report = report;
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { IsEnabled = false };
        _hideTimer.Tick += OnHideTimer;
        _controller.Changed += OnControllerChanged;
    }

    /// <summary>Set by phase 5; null hides the PiP button.</summary>
    public Action<nint>? PipHandler { get; set; }

    public bool Enabled => _getSettings().ToolbarEnabled;

    public bool IsToolbarVisible => _toolbar?.IsVisible == true;

    public nint Target => _target;

    public void Attach(WinEventService winEvents, MouseHookService mouse)
    {
        winEvents.ForegroundChanged += OnForegroundChanged;
        winEvents.LocationChanged += OnLocationChanged;
        winEvents.MinimizeStarted += OnMinimizeStarted;
        winEvents.WindowDestroyed += OnWindowDestroyed;
        mouse.MouseMoved += OnMouseMoved;
    }

    /// <summary>Entry point for every pointer position (from the hook, or directly from the self-test).</summary>
    public void OnMouseMoved(int x, int y)
    {
        if (_disposed)
        {
            return;
        }

        if (!Enabled)
        {
            HideNow();
            return;
        }

        nint foreground = _getForegroundWindow();
        if (foreground != _target)
        {
            // A different window took focus; the toolbar belongs to whichever is in front now (if eligible).
            SetTarget(foreground);
        }

        if (_target == 0)
        {
            ScheduleHide();
            return;
        }

        bool inStrip = HoverLogic.IsInTitleStrip(_targetRect, x, y, _stripHeight);
        bool overToolbar = IsToolbarVisible && _toolbar!.ScreenRect.Contains(x, y);

        switch (HoverLogic.Decide(IsToolbarVisible, inStrip, overToolbar))
        {
            case HoverDecision.Show:
                ShowToolbar();
                break;
            case HoverDecision.KeepVisible:
                _hideTimer.Stop();
                break;
            case HoverDecision.ScheduleHide:
                ScheduleHide();
                break;
        }
    }

    public void HideNow()
    {
        _hideTimer.Stop();
        _toolbar?.HideToolbar();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hideTimer.Stop();
        _controller.Changed -= OnControllerChanged;
        if (_toolbar is not null)
        {
            _toolbar.Close();
            _toolbar = null;
        }
    }

    private void SetTarget(nint hwnd)
    {
        HideNow();
        var probe = _controller.Probe(hwnd);
        if (!probe.IsEligible)
        {
            _target = 0;
            return;
        }

        _target = probe.Root;
        RefreshTargetGeometry();
    }

    private void RefreshTargetGeometry()
    {
        _targetRect = Win32WindowOps.GetFrameBounds(_target);
        _stripHeight = Win32WindowOps.GetCaptionHeight(_target);
    }

    private void ShowToolbar()
    {
        var toolbar = EnsureToolbar();
        BindToolbar(toolbar);

        // First show creates the HWND (off-screen) so we can measure it in physical pixels.
        toolbar.ShowAt(-32000, -32000);
        PositionToolbar(toolbar);
        _hideTimer.Stop();
    }

    private void PositionToolbar(ToolbarWindow toolbar)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var size = toolbar.ScreenRect;
            var workArea = Win32WindowOps.GetWorkArea(_target);
            int inset = (int)Math.Round(TopInsetDip * Win32WindowOps.GetDpi(_target) / 96.0);
            var (x, y) = HoverLogic.PlaceToolbar(_targetRect, size.Width, size.Height, workArea, inset);
            toolbar.ShowAt(x, y);

            // Crossing to a monitor with a different DPI rescales the toolbar; re-place it with the new size.
            if (toolbar.ScreenRect.Width == size.Width && toolbar.ScreenRect.Height == size.Height)
            {
                break;
            }
        }
    }

    private void BindToolbar(ToolbarWindow toolbar)
    {
        bool nativelyTopmost = (_controller.Probe(_target).Snapshot?.IsTopmost) ?? false;
        toolbar.Bind(_controller.Find(_target), nativelyTopmost);
        toolbar.PipAvailable = PipHandler is not null;
    }

    private ToolbarWindow EnsureToolbar()
    {
        if (_toolbar is not null)
        {
            return _toolbar;
        }

        var toolbar = new ToolbarWindow();
        toolbar.PinToggled += () => Run(() => _controller.ToggleTopmost(_target));
        toolbar.OpacityChanged += percent => Run(() => _controller.SetOpacity(_target, percent));
        toolbar.GhostToggled += () => Run(() => _controller.ToggleClickThrough(_target));
        toolbar.ResetRequested += () => Run(() => _controller.Restore(_target));
        toolbar.PipRequested += () =>
        {
            nint target = _target;
            HideNow();
            PipHandler?.Invoke(target);
        };
        _toolbar = toolbar;
        return toolbar;
    }

    private void Run(Func<OpResult> operation)
    {
        if (_target == 0)
        {
            return;
        }

        var result = operation();
        _report(result);
        if (_toolbar is not null && _toolbar.IsVisible)
        {
            BindToolbar(_toolbar);
            _toolbar.BringToTop();
        }
    }

    private void ScheduleHide()
    {
        if (!IsToolbarVisible || _hideTimer.IsEnabled)
        {
            return;
        }

        _hideTimer.Interval = TimeSpan.FromMilliseconds(_getSettings().ToolbarHideDelayMs);
        _hideTimer.Start();
    }

    private void OnHideTimer(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (!IsToolbarVisible)
        {
            return;
        }

        // Re-check at the moment of hiding: the pointer may have come back without a move event.
        var (x, y) = Win32WindowOps.GetCursorPosition();
        bool stillHovering = _target != 0
            && (HoverLogic.IsInTitleStrip(_targetRect, x, y, _stripHeight) || _toolbar!.ScreenRect.Contains(x, y));
        if (!stillHovering)
        {
            _toolbar!.HideToolbar();
        }
    }

    private void OnForegroundChanged(nint hwnd)
    {
        if (hwnd != _target)
        {
            HideNow();
            _target = 0;
        }
    }

    private void OnLocationChanged(nint hwnd)
    {
        if (hwnd != _target || _target == 0)
        {
            return;
        }

        RefreshTargetGeometry();
        if (IsToolbarVisible)
        {
            PositionToolbar(_toolbar!);
        }
    }

    private void OnMinimizeStarted(nint hwnd)
    {
        if (hwnd == _target)
        {
            HideNow();
        }
    }

    private void OnWindowDestroyed(nint hwnd)
    {
        _controller.Forget(hwnd);
        if (hwnd == _target)
        {
            HideNow();
            _target = 0;
        }
    }

    private void OnControllerChanged()
    {
        if (_toolbar is not null && _toolbar.IsVisible && _target != 0)
        {
            BindToolbar(_toolbar);
        }
    }
}

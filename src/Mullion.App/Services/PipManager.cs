using Mullion.App.Interop;
using Mullion.App.UI;
using Mullion.Core;

namespace Mullion.App.Services;

/// <summary>
/// Owns the picture-in-picture frames: one per source window, closed when the source goes away.
/// While a frame is open its source is optionally kept awake (see <see cref="SourceKeepAlive"/>) and locked
/// in the controller so other operations can't fight over its styles.
/// </summary>
internal sealed class PipManager : IDisposable
{
    private readonly WindowController _controller;
    private readonly Win32WindowOps _ops;
    private readonly Func<bool> _keepSourceAwake;
    private readonly Action<string> _notify;
    private readonly Dictionary<nint, PipWindow> _frames = new();
    private readonly Dictionary<nint, SourceKeepAlive> _keepAlives = new();
    private bool _disposed;

    public PipManager(WindowController controller, Win32WindowOps ops, Func<bool> keepSourceAwake, Action<string> notify)
    {
        _controller = controller;
        _ops = ops;
        _keepSourceAwake = keepSourceAwake;
        _notify = notify;
    }

    /// <summary>Alpha applied to a hidden source. Exposed so --pipcheck can compare values.</summary>
    public byte GhostAlpha { get; set; } = PipKeepAlive.DefaultGhostAlpha;

    public int Count => _frames.Count;

    public bool HasFrameFor(nint hwnd) => _frames.ContainsKey(hwnd);

    public bool IsKeptAwake(nint hwnd) => _keepAlives.ContainsKey(hwnd);

    public PipWindow? FrameFor(nint hwnd) => _frames.TryGetValue(hwnd, out var frame) ? frame : null;

    /// <summary>Opens a PiP frame for the window (or its root); closes it if one already exists. Returns true when a frame is now open.</summary>
    public bool Toggle(nint hwnd)
    {
        if (_disposed)
        {
            return false;
        }

        var probe = _controller.Probe(hwnd);
        nint source = probe.Root;
        if (source != 0 && _frames.TryGetValue(source, out var existing))
        {
            existing.Close();
            return false;
        }

        if (!probe.IsEligible || probe.Snapshot is null)
        {
            _notify(OpResult.NotEligible(probe.Verdict).Message);
            return false;
        }

        string title = string.IsNullOrWhiteSpace(probe.Snapshot.Value.Title) ? "Window" : probe.Snapshot.Value.Title;
        bool sourceWasForeground = _ops.GetRootWindow(NativeMethods.GetForegroundWindow()) == source;

        var frame = new PipWindow(source, title);
        frame.OpenRequested += OnOpenRequested;
        frame.Closed += (_, _) => OnFrameClosed(source, frame);

        _frames[source] = frame;
        frame.Show();
        frame.PlaceInitial();

        if (!frame.ThumbnailRegistered)
        {
            _notify($"Windows could not create a live thumbnail of \"{title}\" (DWM error 0x{frame.RegisterResult:X8}).");
            frame.Close();
            return false;
        }

        if (_keepSourceAwake())
        {
            var keepAlive = SourceKeepAlive.TryApply(_ops, source, GhostAlpha, out string? problem);
            if (keepAlive is not null)
            {
                _keepAlives[source] = keepAlive;
                _controller.Suspend(source);

                // The hidden source is now at the top of the topmost band; keep the frame above it.
                frame.BringToTop();

                // The source is invisible but would still have keyboard focus; hand focus to the window behind it.
                if (sourceWasForeground)
                {
                    ActivateNextWindowBelow(source);
                }
            }
            else if (problem is not null)
            {
                _notify(problem);
            }
        }

        return true;
    }

    /// <summary>Called from the WinEvent hook: a destroyed source takes its frame with it.</summary>
    public void OnWindowDestroyed(nint hwnd)
    {
        if (_frames.TryGetValue(hwnd, out var frame))
        {
            frame.Close();
        }
    }

    public void CloseAll()
    {
        foreach (var frame in _frames.Values.ToArray())
        {
            frame.Close();
        }

        _frames.Clear();

        // Belt and braces: anything still hidden (a frame that failed to close) is restored here.
        foreach (var source in _keepAlives.Keys.ToArray())
        {
            RestoreSource(source);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseAll();
    }

    private void OnFrameClosed(nint source, PipWindow frame)
    {
        if (_frames.TryGetValue(source, out var current) && ReferenceEquals(current, frame))
        {
            _frames.Remove(source);
        }

        RestoreSource(source);
    }

    private void RestoreSource(nint source)
    {
        if (_keepAlives.Remove(source, out var keepAlive))
        {
            keepAlive.Restore();
        }

        _controller.Resume(source);
    }

    private void OnOpenRequested(PipWindow frame)
    {
        // Close first: that un-hides the source. Then bring it forward.
        nint source = frame.Source;
        frame.Close();
        Win32WindowOps.RestoreAndActivate(source);
    }

    private void ActivateNextWindowBelow(nint source)
    {
        bool passedSource = false;
        foreach (nint hwnd in Win32WindowOps.EnumerateTopLevelWindows())
        {
            if (!passedSource)
            {
                passedSource = hwnd == source;
                continue;
            }

            if (_frames.ContainsKey(hwnd) || _keepAlives.ContainsKey(hwnd) || Win32WindowOps.IsMinimized(hwnd))
            {
                continue;
            }

            // Own windows are excluded by the controller's filter, so this never picks the toolbar or a PiP frame.
            if (_controller.Probe(hwnd).IsEligible)
            {
                NativeMethods.SetForegroundWindow(hwnd);
                return;
            }
        }
    }
}

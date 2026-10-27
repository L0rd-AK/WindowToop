using Mullion.App.Interop;
using Mullion.App.UI;
using Mullion.Core;

namespace Mullion.App.Services;

/// <summary>Owns the picture-in-picture frames: one per source window, closed when the source goes away.</summary>
internal sealed class PipManager : IDisposable
{
    private readonly WindowController _controller;
    private readonly Action<string> _notify;
    private readonly Dictionary<nint, PipWindow> _frames = new();
    private bool _disposed;

    public PipManager(WindowController controller, Action<string> notify)
    {
        _controller = controller;
        _notify = notify;
    }

    public int Count => _frames.Count;

    public bool HasFrameFor(nint hwnd) => _frames.ContainsKey(hwnd);

    /// <summary>Opens a PiP frame for the window (or its root); closes it if one already exists. Returns true when a frame is now open.</summary>
    public bool Toggle(nint hwnd)
    {
        if (_disposed)
        {
            return false;
        }

        var probe = _controller.Probe(hwnd);
        if (!probe.IsEligible || probe.Snapshot is null)
        {
            _notify(OpResult.NotEligible(probe.Verdict).Message);
            return false;
        }

        nint source = probe.Root;
        if (_frames.TryGetValue(source, out var existing))
        {
            existing.Close();
            return false;
        }

        string title = string.IsNullOrWhiteSpace(probe.Snapshot.Value.Title) ? "Window" : probe.Snapshot.Value.Title;
        var frame = new PipWindow(source, title);
        frame.OpenRequested += OnOpenRequested;
        frame.Closed += (_, _) =>
        {
            if (_frames.TryGetValue(source, out var current) && ReferenceEquals(current, frame))
            {
                _frames.Remove(source);
            }
        };

        _frames[source] = frame;
        frame.Show();
        frame.PlaceInitial();

        if (!frame.ThumbnailRegistered)
        {
            _notify($"Windows could not create a live thumbnail of \"{title}\" (DWM error 0x{frame.RegisterResult:X8}).");
            frame.Close();
            return false;
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

    private static void OnOpenRequested(PipWindow frame)
    {
        Win32WindowOps.RestoreAndActivate(frame.Source);
        frame.Close();
    }
}

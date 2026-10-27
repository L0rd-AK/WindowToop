using Mullion.Core;

namespace Mullion.Tests;

/// <summary>In-memory stand-in for Win32 so controller behaviour can be tested without a desktop.</summary>
internal sealed class FakeWindowOps : IWindowOps
{
    public const int ErrorInvalidParameter = 87;

    public sealed class FakeWindow
    {
        public nint Hwnd { get; init; }

        /// <summary>0 means the window is its own root.</summary>
        public nint Root { get; set; }

        public bool Visible { get; set; } = true;

        public uint ExStyle { get; set; }

        public string Title { get; set; } = "Untitled app";

        public string ClassName { get; set; } = "AppWindowClass";

        public uint ProcessId { get; set; } = 4242;

        /// <summary>Alpha last applied with SetLayeredWindowAttributes; null if never called.</summary>
        public byte? Alpha { get; set; }

        /// <summary>Simulates a higher-integrity (elevated) target: every mutating call fails with ERROR_ACCESS_DENIED.</summary>
        public bool Elevated { get; set; }

        /// <summary>Simulates a WPF window, whose WM_STYLECHANGING handler strips WS_EX_LAYERED again.</summary>
        public bool RejectsLayered { get; set; }

        public int RedrawCount { get; set; }
    }

    public Dictionary<nint, FakeWindow> Windows { get; } = new();

    public List<string> Calls { get; } = new();

    public int LastError { get; private set; }

    public FakeWindow Add(nint hwnd, Action<FakeWindow>? configure = null)
    {
        var window = new FakeWindow { Hwnd = hwnd };
        configure?.Invoke(window);
        Windows[hwnd] = window;
        return window;
    }

    public bool IsWindow(nint hwnd) => Windows.ContainsKey(hwnd);

    public nint GetRootWindow(nint hwnd) =>
        Windows.TryGetValue(hwnd, out var w) && w.Root != 0 ? w.Root : hwnd;

    public WindowSnapshot? TryGetSnapshot(nint hwnd)
    {
        if (!Windows.TryGetValue(hwnd, out var w))
        {
            return null;
        }

        return new WindowSnapshot(hwnd, GetRootWindow(hwnd), w.Visible, w.ExStyle, w.Title, w.ClassName, w.ProcessId);
    }

    public uint GetExStyle(nint hwnd) => Windows.TryGetValue(hwnd, out var w) ? w.ExStyle : 0;

    public bool SetExStyle(nint hwnd, uint exStyle)
    {
        Calls.Add($"SetExStyle({hwnd},0x{exStyle:X})");
        if (!Windows.TryGetValue(hwnd, out var w))
        {
            return Fail(1400); // ERROR_INVALID_WINDOW_HANDLE
        }

        if (w.Elevated)
        {
            return Fail(IWindowOps.ErrorAccessDenied);
        }

        // Dropping WS_EX_LAYERED discards the layered attributes, as Windows does.
        if ((w.ExStyle & WindowStyles.WS_EX_LAYERED) != 0 && (exStyle & WindowStyles.WS_EX_LAYERED) == 0)
        {
            w.Alpha = null;
        }

        if (w.RejectsLayered && (exStyle & WindowStyles.WS_EX_LAYERED) != 0)
        {
            // The write "succeeds" but the window keeps the bit off; the real ops layer reads back and reports this.
            w.ExStyle = exStyle & ~WindowStyles.WS_EX_LAYERED;
            return Fail(IWindowOps.ErrorStyleRejected);
        }

        w.ExStyle = exStyle;
        LastError = 0;
        return true;
    }

    public bool SetTopmost(nint hwnd, bool topmost)
    {
        Calls.Add($"SetTopmost({hwnd},{topmost})");
        if (!Windows.TryGetValue(hwnd, out var w))
        {
            return Fail(1400);
        }

        if (w.Elevated)
        {
            return Fail(IWindowOps.ErrorAccessDenied);
        }

        w.ExStyle = topmost ? w.ExStyle | WindowStyles.WS_EX_TOPMOST : w.ExStyle & ~WindowStyles.WS_EX_TOPMOST;
        LastError = 0;
        return true;
    }

    public bool SetLayeredAlpha(nint hwnd, byte alpha)
    {
        Calls.Add($"SetLayeredAlpha({hwnd},{alpha})");
        if (!Windows.TryGetValue(hwnd, out var w))
        {
            return Fail(1400);
        }

        if (w.Elevated)
        {
            return Fail(IWindowOps.ErrorAccessDenied);
        }

        if ((w.ExStyle & WindowStyles.WS_EX_LAYERED) == 0)
        {
            return Fail(ErrorInvalidParameter);
        }

        w.Alpha = alpha;
        LastError = 0;
        return true;
    }

    public void Redraw(nint hwnd)
    {
        Calls.Add($"Redraw({hwnd})");
        if (Windows.TryGetValue(hwnd, out var w))
        {
            w.RedrawCount++;
        }
    }

    private bool Fail(int error)
    {
        LastError = error;
        return false;
    }
}

namespace Mullion.Core;

/// <summary>
/// The minimal set of window operations the controller needs. The app implements this over Win32;
/// tests implement it with an in-memory fake. Every mutating call returns false on failure and
/// leaves the Win32 error code in <see cref="LastError"/>.
/// </summary>
public interface IWindowOps
{
    /// <summary>ERROR_ACCESS_DENIED, raised by UIPI when the target runs at a higher integrity level.</summary>
    const int ErrorAccessDenied = 5;

    /// <summary>
    /// ERROR_NOT_SUPPORTED, reported by <see cref="SetExStyle"/> when the call "succeeds" but the window
    /// silently discards the managed bits. WPF windows do this: their HwndTarget strips WS_EX_LAYERED in
    /// WM_STYLECHANGING unless the window itself uses per-pixel opacity.
    /// </summary>
    const int ErrorStyleRejected = 50;

    /// <summary>Win32 error code from the most recent failed call (0 if the last call succeeded).</summary>
    int LastError { get; }

    bool IsWindow(nint hwnd);

    /// <summary>GetAncestor(hwnd, GA_ROOT). Returns hwnd itself when it is already top-level.</summary>
    nint GetRootWindow(nint hwnd);

    /// <summary>Reads the properties the filter needs. Returns null when the handle is invalid.</summary>
    WindowSnapshot? TryGetSnapshot(nint hwnd);

    uint GetExStyle(nint hwnd);

    /// <summary>
    /// Writes GWL_EXSTYLE and verifies the managed bits (layered / transparent) actually took effect.
    /// Returns false with <see cref="ErrorStyleRejected"/> when the window overrode them.
    /// </summary>
    bool SetExStyle(nint hwnd, uint exStyle);

    /// <summary>SetWindowPos with HWND_TOPMOST / HWND_NOTOPMOST and SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE.</summary>
    bool SetTopmost(nint hwnd, bool topmost);

    /// <summary>SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA).</summary>
    bool SetLayeredAlpha(nint hwnd, byte alpha);

    /// <summary>Forces a repaint after WS_EX_LAYERED is removed (otherwise the window may stay stale).</summary>
    void Redraw(nint hwnd);
}

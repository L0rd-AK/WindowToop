namespace Mullion.Core;

/// <summary>
/// The handful of Win32 style bits the core logic reasons about.
/// Kept here (not in the app's NativeMethods) so the pure logic and its tests never need Win32.
/// </summary>
public static class WindowStyles
{
    /// <summary>WS_EX_TOPMOST: the window sits above all non-topmost windows.</summary>
    public const uint WS_EX_TOPMOST = 0x00000008;

    /// <summary>WS_EX_TRANSPARENT: mouse input passes through to windows beneath (needs WS_EX_LAYERED).</summary>
    public const uint WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>WS_EX_TOOLWINDOW: floating tool palette, not shown in the taskbar/Alt-Tab.</summary>
    public const uint WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>WS_EX_LAYERED: enables per-window alpha via SetLayeredWindowAttributes.</summary>
    public const uint WS_EX_LAYERED = 0x00080000;

    /// <summary>WS_EX_NOACTIVATE: the window never becomes the foreground window on click.</summary>
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>The bits Mullion itself toggles on foreign windows (used when restoring).</summary>
    public const uint ManagedBits = WS_EX_LAYERED | WS_EX_TRANSPARENT;
}

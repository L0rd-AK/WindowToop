namespace Mullion.Core;

/// <summary>
/// An immutable read of the properties the filter needs. Produced by the app from Win32,
/// or by tests directly.
/// </summary>
/// <param name="Hwnd">Handle the snapshot was taken for.</param>
/// <param name="RootHwnd">GetAncestor(hwnd, GA_ROOT): the top-level owner of the handle.</param>
/// <param name="IsVisible">IsWindowVisible result.</param>
/// <param name="ExStyle">GWL_EXSTYLE bits.</param>
/// <param name="Title">Window text (may be empty).</param>
/// <param name="ClassName">Window class name.</param>
/// <param name="ProcessId">Owning process id.</param>
public readonly record struct WindowSnapshot(
    nint Hwnd,
    nint RootHwnd,
    bool IsVisible,
    uint ExStyle,
    string Title,
    string ClassName,
    uint ProcessId)
{
    /// <summary>True when the window is a UWP / Store app host frame. Opacity is often ignored there.</summary>
    public bool IsUwpFrame => string.Equals(ClassName, "ApplicationFrameWindow", StringComparison.Ordinal);

    /// <summary>True when the window already had WS_EX_LAYERED before Mullion touched it.</summary>
    public bool IsLayered => (ExStyle & WindowStyles.WS_EX_LAYERED) != 0;

    public bool IsTopmost => (ExStyle & WindowStyles.WS_EX_TOPMOST) != 0;

    public bool IsToolWindow => (ExStyle & WindowStyles.WS_EX_TOOLWINDOW) != 0;
}

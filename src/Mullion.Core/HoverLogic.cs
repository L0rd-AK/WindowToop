namespace Mullion.Core;

/// <summary>A rectangle in physical screen pixels (what GetWindowRect / DWM return).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public static PixelRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);
}

/// <summary>What the toolbar coordinator should do after a mouse move.</summary>
public enum HoverDecision
{
    /// <summary>Toolbar hidden, pointer not on a title bar: nothing to do.</summary>
    None,

    /// <summary>Pointer entered the title strip while the toolbar was hidden.</summary>
    Show,

    /// <summary>Toolbar visible and pointer still over the strip or the toolbar: cancel any pending hide.</summary>
    KeepVisible,

    /// <summary>Toolbar visible but pointer left both areas: start the hide timer.</summary>
    ScheduleHide,
}

/// <summary>Pure geometry for the floating toolbar: where to show it and when.</summary>
public static class HoverLogic
{
    /// <summary>True when the point lies on the top <paramref name="stripHeight"/> pixels of the window.</summary>
    public static bool IsInTitleStrip(PixelRect window, int x, int y, int stripHeight)
    {
        if (window.IsEmpty || stripHeight <= 0)
        {
            return false;
        }

        return x >= window.Left && x < window.Right && y >= window.Top && y < window.Top + stripHeight;
    }

    public static HoverDecision Decide(bool toolbarVisible, bool inStrip, bool overToolbar)
    {
        if (!toolbarVisible)
        {
            return inStrip ? HoverDecision.Show : HoverDecision.None;
        }

        return inStrip || overToolbar ? HoverDecision.KeepVisible : HoverDecision.ScheduleHide;
    }

    /// <summary>
    /// Centers the toolbar on the window's top edge, <paramref name="topInset"/> pixels down, and clamps it
    /// to the monitor work area so it never ends up off-screen.
    /// </summary>
    public static (int X, int Y) PlaceToolbar(PixelRect window, int toolbarWidth, int toolbarHeight, PixelRect monitor, int topInset)
    {
        int x = window.Left + (window.Width - toolbarWidth) / 2;
        int y = window.Top + topInset;

        if (!monitor.IsEmpty)
        {
            x = Math.Clamp(x, monitor.Left, Math.Max(monitor.Left, monitor.Right - toolbarWidth));
            y = Math.Clamp(y, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - toolbarHeight));
        }

        return (x, y);
    }
}

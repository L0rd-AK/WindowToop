namespace Mullion.Core;

/// <summary>
/// Keeps a picture-in-picture source "visible" to its own app while the user works elsewhere.
///
/// Chromium-based browsers and Firefox stop rendering (video, requestAnimationFrame) as soon as their
/// window is fully covered by other opaque windows, which freezes the DWM thumbnail. While a PiP view is
/// open, Mullion makes the source topmost (so nothing normal can cover it), click-through, and almost
/// fully transparent (so the user doesn't see it). Browsers only check what covers them, not their own
/// alpha, so they keep rendering. Everything is put back when the view closes.
/// </summary>
public static class PipKeepAlive
{
    /// <summary>Alpha used for the hidden source. 1 rather than 0: DWM may skip composing fully transparent windows.</summary>
    public const byte DefaultGhostAlpha = 1;

    /// <summary>Extended style while the source is hidden: layered (for alpha) and transparent (clicks fall through).</summary>
    public static uint GhostExStyle(uint original) => original | WindowStyles.WS_EX_LAYERED | WindowStyles.WS_EX_TRANSPARENT;

    /// <summary>Puts back only the bits keep-alive manages; anything else the app changed meanwhile is kept.</summary>
    public static uint RestoreExStyle(uint current, uint original) =>
        (current & ~WindowStyles.ManagedBits) | (original & WindowStyles.ManagedBits);

    /// <summary>
    /// Whether the source can be hidden this way. A window that is already layered can only be handled if its
    /// layering is a plain constant alpha (set with SetLayeredWindowAttributes, by the app or by Mullion), because
    /// that is the only kind we can read back and restore. Windows painted with UpdateLayeredWindow cannot be queried.
    /// </summary>
    /// <param name="exStyle">Current GWL_EXSTYLE.</param>
    /// <param name="layeredQuery">Result of GetLayeredWindowAttributes: null if the call failed, otherwise its flags.</param>
    public static bool CanGhost(uint exStyle, uint? layeredQuery)
    {
        if ((exStyle & WindowStyles.WS_EX_LAYERED) == 0)
        {
            return true;
        }

        const uint LwaColorKey = 0x1;
        return layeredQuery is { } flags && (flags & LwaColorKey) == 0;
    }
}

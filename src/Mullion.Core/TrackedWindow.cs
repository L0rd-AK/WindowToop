namespace Mullion.Core;

/// <summary>
/// A foreign window Mullion has modified, together with the state needed to put it back.
/// </summary>
public sealed class TrackedWindow
{
    public TrackedWindow(nint hwnd, string title, string className, uint originalExStyle)
    {
        Hwnd = hwnd;
        Title = title;
        ClassName = className;
        OriginalExStyle = originalExStyle;
        IsTopmost = OriginallyTopmost;
    }

    public nint Hwnd { get; }

    public string Title { get; internal set; }

    public string ClassName { get; }

    /// <summary>GWL_EXSTYLE as it was before Mullion's first change.</summary>
    public uint OriginalExStyle { get; }

    public bool OriginallyTopmost => (OriginalExStyle & WindowStyles.WS_EX_TOPMOST) != 0;

    /// <summary>The app itself used layered rendering; Mullion must not call SetLayeredWindowAttributes on it.</summary>
    public bool OriginallyLayered => (OriginalExStyle & WindowStyles.WS_EX_LAYERED) != 0;

    public bool IsTopmost { get; internal set; }

    /// <summary>10-100. 100 means Mullion is not applying any alpha.</summary>
    public int Opacity { get; internal set; } = OpacityMath.Max;

    public bool ClickThrough { get; internal set; }

    public bool IsUwpFrame => string.Equals(ClassName, "ApplicationFrameWindow", StringComparison.Ordinal);

    /// <summary>True while any property differs from the window's original state.</summary>
    public bool IsModified => IsTopmost != OriginallyTopmost || Opacity != OpacityMath.Max || ClickThrough;

    public override string ToString()
    {
        var parts = new List<string>(3);
        if (IsTopmost != OriginallyTopmost)
        {
            parts.Add(IsTopmost ? "pinned" : "unpinned");
        }

        if (Opacity != OpacityMath.Max)
        {
            parts.Add($"{Opacity}%");
        }

        if (ClickThrough)
        {
            parts.Add("click-through");
        }

        return parts.Count == 0 ? Title : $"{Title} ({string.Join(", ", parts)})";
    }
}

namespace Mullion.Core;

/// <summary>Why a window was (or was not) accepted as a target.</summary>
public enum FilterVerdict
{
    Eligible,
    NullHandle,
    NotTopLevel,
    NotVisible,
    ToolWindow,
    EmptyTitle,
    OwnWindow,
    ShellWindow,
}

/// <summary>
/// Decides which windows Mullion is allowed to touch. Pure function of a <see cref="WindowSnapshot"/>.
/// </summary>
public static class WindowFilter
{
    /// <summary>Window classes that belong to the shell (desktop, taskbar). Never modify these.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
    };

    /// <param name="snapshot">The window to evaluate. Should already be the GA_ROOT ancestor.</param>
    /// <param name="ownProcessId">Mullion's own process id; 0 disables the own-window check (self-test only).</param>
    public static FilterVerdict Evaluate(in WindowSnapshot snapshot, uint ownProcessId)
    {
        if (snapshot.Hwnd == 0)
        {
            return FilterVerdict.NullHandle;
        }

        if (snapshot.RootHwnd != 0 && snapshot.RootHwnd != snapshot.Hwnd)
        {
            return FilterVerdict.NotTopLevel;
        }

        if (ownProcessId != 0 && snapshot.ProcessId == ownProcessId)
        {
            return FilterVerdict.OwnWindow;
        }

        if (!snapshot.IsVisible)
        {
            return FilterVerdict.NotVisible;
        }

        if (snapshot.IsToolWindow)
        {
            return FilterVerdict.ToolWindow;
        }

        if (ShellClasses.Contains(snapshot.ClassName))
        {
            return FilterVerdict.ShellWindow;
        }

        if (string.IsNullOrWhiteSpace(snapshot.Title))
        {
            return FilterVerdict.EmptyTitle;
        }

        return FilterVerdict.Eligible;
    }

    public static string Describe(FilterVerdict verdict) => verdict switch
    {
        FilterVerdict.Eligible => "eligible",
        FilterVerdict.NullHandle => "no window",
        FilterVerdict.NotTopLevel => "not a top-level window",
        FilterVerdict.NotVisible => "window is hidden",
        FilterVerdict.ToolWindow => "tool windows are skipped",
        FilterVerdict.EmptyTitle => "untitled windows are skipped",
        FilterVerdict.OwnWindow => "that is one of Mullion's own windows",
        FilterVerdict.ShellWindow => "desktop and taskbar windows are skipped",
        _ => verdict.ToString(),
    };
}

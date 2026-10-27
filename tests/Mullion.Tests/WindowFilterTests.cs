using Mullion.Core;

namespace Mullion.Tests;

public class WindowFilterTests
{
    private const uint OwnPid = 77;

    private static WindowSnapshot Snapshot(
        nint hwnd = 0x10,
        nint root = 0x10,
        bool visible = true,
        uint exStyle = 0,
        string title = "Document - Editor",
        string className = "EditorFrame",
        uint pid = 5000) => new(hwnd, root, visible, exStyle, title, className, pid);

    [Fact]
    public void OrdinaryVisibleTitledWindow_IsEligible() =>
        Assert.Equal(FilterVerdict.Eligible, WindowFilter.Evaluate(Snapshot(), OwnPid));

    [Fact]
    public void NullHandle_IsRejected() =>
        Assert.Equal(FilterVerdict.NullHandle, WindowFilter.Evaluate(Snapshot(hwnd: 0, root: 0), OwnPid));

    [Fact]
    public void ChildWindow_IsNotTopLevel() =>
        Assert.Equal(FilterVerdict.NotTopLevel, WindowFilter.Evaluate(Snapshot(hwnd: 0x20, root: 0x10), OwnPid));

    [Fact]
    public void HiddenWindow_IsRejected() =>
        Assert.Equal(FilterVerdict.NotVisible, WindowFilter.Evaluate(Snapshot(visible: false), OwnPid));

    [Fact]
    public void ToolWindow_IsRejected() =>
        Assert.Equal(FilterVerdict.ToolWindow, WindowFilter.Evaluate(Snapshot(exStyle: WindowStyles.WS_EX_TOOLWINDOW), OwnPid));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UntitledWindow_IsRejected(string title) =>
        Assert.Equal(FilterVerdict.EmptyTitle, WindowFilter.Evaluate(Snapshot(title: title), OwnPid));

    [Fact]
    public void OwnProcessWindow_IsRejected() =>
        Assert.Equal(FilterVerdict.OwnWindow, WindowFilter.Evaluate(Snapshot(pid: OwnPid), OwnPid));

    [Fact]
    public void OwnProcessCheck_CanBeDisabledWithZero() =>
        Assert.Equal(FilterVerdict.Eligible, WindowFilter.Evaluate(Snapshot(pid: OwnPid), 0));

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void ShellWindows_AreRejected(string className) =>
        Assert.Equal(FilterVerdict.ShellWindow, WindowFilter.Evaluate(Snapshot(title: "Program Manager", className: className), OwnPid));

    [Fact]
    public void OwnWindowCheck_TakesPrecedenceOverVisibility() =>
        Assert.Equal(FilterVerdict.OwnWindow, WindowFilter.Evaluate(Snapshot(visible: false, pid: OwnPid), OwnPid));

    [Fact]
    public void LayeredAndTopmostFlags_DoNotAffectEligibility() =>
        Assert.Equal(
            FilterVerdict.Eligible,
            WindowFilter.Evaluate(Snapshot(exStyle: WindowStyles.WS_EX_LAYERED | WindowStyles.WS_EX_TOPMOST), OwnPid));

    [Fact]
    public void Snapshot_DerivedFlags()
    {
        var s = Snapshot(exStyle: WindowStyles.WS_EX_LAYERED | WindowStyles.WS_EX_TOPMOST, className: "ApplicationFrameWindow");

        Assert.True(s.IsLayered);
        Assert.True(s.IsTopmost);
        Assert.True(s.IsUwpFrame);
        Assert.False(s.IsToolWindow);
    }

    [Theory]
    [InlineData(FilterVerdict.Eligible)]
    [InlineData(FilterVerdict.NullHandle)]
    [InlineData(FilterVerdict.NotTopLevel)]
    [InlineData(FilterVerdict.NotVisible)]
    [InlineData(FilterVerdict.ToolWindow)]
    [InlineData(FilterVerdict.EmptyTitle)]
    [InlineData(FilterVerdict.OwnWindow)]
    [InlineData(FilterVerdict.ShellWindow)]
    public void EveryVerdict_HasADescription(FilterVerdict verdict) =>
        Assert.False(string.IsNullOrWhiteSpace(WindowFilter.Describe(verdict)));
}

using Mullion.Core;

namespace Mullion.Tests;

public class WindowControllerTests
{
    private const uint OwnPid = 999;
    private const nint H = 0x1000;

    private static (WindowController Controller, FakeWindowOps Ops, FakeWindowOps.FakeWindow Window) Setup(Action<FakeWindowOps.FakeWindow>? configure = null)
    {
        var ops = new FakeWindowOps();
        var window = ops.Add(H, configure);
        return (new WindowController(ops, OwnPid), ops, window);
    }

    [Fact]
    public void ToggleTopmost_PinsThenUnpins_AndUntracksWhenBackToOriginal()
    {
        var (controller, _, window) = Setup();

        var pin = controller.ToggleTopmost(H);

        Assert.Equal(OpStatus.Ok, pin.Status);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_TOPMOST) != 0);
        Assert.Single(controller.TrackedWindows);
        Assert.True(controller.Find(H)!.IsTopmost);

        var unpin = controller.ToggleTopmost(H);

        Assert.Equal(OpStatus.Ok, unpin.Status);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_TOPMOST) == 0);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void ToggleTopmost_OnOriginallyTopmostWindow_UnpinsAndRestoreRepins()
    {
        var (controller, _, window) = Setup(w => w.ExStyle = WindowStyles.WS_EX_TOPMOST);

        controller.ToggleTopmost(H);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_TOPMOST) == 0);
        Assert.Single(controller.TrackedWindows);

        controller.Restore(H);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_TOPMOST) != 0);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void SetOpacity_AddsLayeredStyleAndAlpha()
    {
        var (controller, _, window) = Setup();

        var result = controller.SetOpacity(H, 50);

        Assert.Equal(OpStatus.Ok, result.Status);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_LAYERED) != 0);
        Assert.Equal((byte)128, window.Alpha);
        Assert.Equal(50, controller.GetOpacity(H));
    }

    [Fact]
    public void SetOpacity_BackTo100_RemovesLayeredRedrawsAndUntracks()
    {
        var (controller, _, window) = Setup();
        controller.SetOpacity(H, 40);

        var result = controller.SetOpacity(H, 100);

        Assert.Equal(OpStatus.Ok, result.Status);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_LAYERED) == 0);
        Assert.Null(window.Alpha);
        Assert.Equal(1, window.RedrawCount);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void SetOpacity_ClampsToRange()
    {
        var (controller, _, window) = Setup();

        controller.SetOpacity(H, 3);
        Assert.Equal(OpacityMath.Min, controller.GetOpacity(H));
        Assert.Equal(OpacityMath.ToAlpha(OpacityMath.Min), window.Alpha);

        controller.SetOpacity(H, 500);
        Assert.Equal(OpacityMath.Max, controller.GetOpacity(H));
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void AdjustOpacity_StepsDownAndStopsAtMinimum()
    {
        var (controller, _, _) = Setup();

        controller.AdjustOpacity(H, -10);
        Assert.Equal(90, controller.GetOpacity(H));

        controller.SetOpacity(H, 15);
        controller.AdjustOpacity(H, -10);
        Assert.Equal(10, controller.GetOpacity(H));

        var atFloor = controller.AdjustOpacity(H, -10);
        Assert.Equal(OpStatus.Ok, atFloor.Status);
        Assert.Equal(10, controller.GetOpacity(H));

        controller.AdjustOpacity(H, 10);
        Assert.Equal(20, controller.GetOpacity(H));
    }

    [Fact]
    public void SetOpacity_SkipsWindowsThatWereAlreadyLayered()
    {
        var (controller, ops, window) = Setup(w => w.ExStyle = WindowStyles.WS_EX_LAYERED);

        var result = controller.SetOpacity(H, 50);

        Assert.Equal(OpStatus.Skipped, result.Status);
        Assert.Null(window.Alpha);
        Assert.DoesNotContain(ops.Calls, c => c.StartsWith("SetLayeredAlpha", StringComparison.Ordinal));
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void ClickThrough_AddsLayeredAndTransparent_ThenRemovesBoth()
    {
        var (controller, _, window) = Setup();

        var on = controller.ToggleClickThrough(H);

        Assert.Equal(OpStatus.Ok, on.Status);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_TRANSPARENT) != 0);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_LAYERED) != 0);
        Assert.Equal((byte)255, window.Alpha);
        Assert.True(controller.Find(H)!.ClickThrough);

        var off = controller.ToggleClickThrough(H);

        Assert.Equal(OpStatus.Ok, off.Status);
        Assert.Equal(0u, window.ExStyle & WindowStyles.ManagedBits);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void ClickThrough_OnAlreadyLayeredWindow_OnlyTogglesTransparentBit()
    {
        var (controller, ops, window) = Setup(w => w.ExStyle = WindowStyles.WS_EX_LAYERED);

        controller.ToggleClickThrough(H);
        Assert.Equal(WindowStyles.WS_EX_LAYERED | WindowStyles.WS_EX_TRANSPARENT, window.ExStyle);
        Assert.DoesNotContain(ops.Calls, c => c.StartsWith("SetLayeredAlpha", StringComparison.Ordinal));

        controller.ToggleClickThrough(H);
        Assert.Equal(WindowStyles.WS_EX_LAYERED, window.ExStyle);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void ClickThroughOff_KeepsLayeredWhileOpacityIsBelow100()
    {
        var (controller, _, window) = Setup();
        controller.SetOpacity(H, 60);
        controller.SetClickThrough(H, true);

        controller.SetClickThrough(H, false);

        Assert.True((window.ExStyle & WindowStyles.WS_EX_LAYERED) != 0);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_TRANSPARENT) == 0);
        Assert.Equal(OpacityMath.ToAlpha(60), window.Alpha);
        Assert.Single(controller.TrackedWindows);
    }

    [Fact]
    public void Opacity100_WhileClickThrough_KeepsLayeredWithFullAlpha()
    {
        var (controller, _, window) = Setup();
        controller.SetOpacity(H, 60);
        controller.SetClickThrough(H, true);

        controller.SetOpacity(H, 100);

        Assert.True((window.ExStyle & WindowStyles.WS_EX_LAYERED) != 0);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_TRANSPARENT) != 0);
        Assert.Equal((byte)255, window.Alpha);
    }

    [Fact]
    public void RestoreAll_PutsEverythingBack()
    {
        var (controller, _, window) = Setup(w => w.ExStyle = 0x00000100); // WS_EX_WINDOWEDGE
        controller.ToggleTopmost(H);
        controller.SetOpacity(H, 30);
        controller.SetClickThrough(H, true);

        int restored = controller.RestoreAll();

        Assert.Equal(1, restored);
        Assert.Equal(0x00000100u, window.ExStyle);
        Assert.Null(window.Alpha);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void Restore_LeavesForeignStyleBitsTheAppChangedMeanwhileAlone()
    {
        const uint acceptFiles = 0x00000010;
        var (controller, _, window) = Setup();
        controller.SetOpacity(H, 30);

        window.ExStyle |= acceptFiles; // the app changed its own style while we were layered

        controller.Restore(H);

        Assert.Equal(acceptFiles, window.ExStyle);
    }

    [Fact]
    public void ElevatedWindow_ReportsAccessDeniedAndIsNotTracked()
    {
        var (controller, _, window) = Setup(w => w.Elevated = true);

        var topmost = controller.ToggleTopmost(H);
        var opacity = controller.SetOpacity(H, 50);
        var click = controller.ToggleClickThrough(H);

        Assert.Equal(OpStatus.AccessDenied, topmost.Status);
        Assert.Equal(OpStatus.AccessDenied, opacity.Status);
        Assert.Equal(OpStatus.AccessDenied, click.Status);
        Assert.Equal(0u, window.ExStyle);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void AlphaFailureAfterAddingLayered_RollsBackTheStyle()
    {
        var ops = new FailingAlphaOps();
        ops.Add(H);
        var controller = new WindowController(ops, OwnPid);

        var result = controller.SetOpacity(H, 50);

        Assert.Equal(OpStatus.Failed, result.Status);
        Assert.Equal(0u, ops.Windows[H].ExStyle);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void WindowThatStripsLayeredStyle_IsReportedAsSkippedAndLeftClean()
    {
        var (controller, _, window) = Setup(w =>
        {
            w.ClassName = "HwndWrapper[SomeWpfApp.exe;;guid]";
            w.RejectsLayered = true;
        });

        var opacity = controller.SetOpacity(H, 50);
        var click = controller.ToggleClickThrough(H);

        Assert.Equal(OpStatus.Skipped, opacity.Status);
        Assert.Equal(OpStatus.Skipped, click.Status);
        Assert.Contains("WPF", opacity.Message, StringComparison.Ordinal);
        Assert.Equal(0u, window.ExStyle); // no stray WS_EX_TRANSPARENT left behind
        Assert.Null(window.Alpha);
        Assert.Empty(controller.TrackedWindows);

        // Always-on-top does not need the layered style and still works.
        Assert.Equal(OpStatus.Ok, controller.ToggleTopmost(H).Status);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_TOPMOST) != 0);
    }

    [Fact]
    public void UwpFrame_SucceedsWithWarning()
    {
        var (controller, _, _) = Setup(w => w.ClassName = "ApplicationFrameWindow");

        var result = controller.SetOpacity(H, 50);

        Assert.Equal(OpStatus.Ok, result.Status);
        Assert.NotNull(result.Warning);
    }

    [Theory]
    [InlineData(FilterVerdict.OwnWindow)]
    [InlineData(FilterVerdict.ToolWindow)]
    [InlineData(FilterVerdict.NotVisible)]
    [InlineData(FilterVerdict.EmptyTitle)]
    public void IneligibleWindows_AreRefusedAndUntouched(FilterVerdict expected)
    {
        var (controller, _, window) = Setup(w =>
        {
            switch (expected)
            {
                case FilterVerdict.OwnWindow:
                    w.ProcessId = OwnPid;
                    break;
                case FilterVerdict.ToolWindow:
                    w.ExStyle = WindowStyles.WS_EX_TOOLWINDOW;
                    break;
                case FilterVerdict.NotVisible:
                    w.Visible = false;
                    break;
                case FilterVerdict.EmptyTitle:
                    w.Title = "   ";
                    break;
            }
        });
        uint before = window.ExStyle;

        var result = controller.ToggleTopmost(H);

        Assert.Equal(OpStatus.NotEligible, result.Status);
        Assert.Equal(before, window.ExStyle);
        Assert.Empty(controller.TrackedWindows);
        Assert.Equal(expected, controller.Probe(H).Verdict);
    }

    [Fact]
    public void ChildHandle_ResolvesToRootWindow()
    {
        var ops = new FakeWindowOps();
        var root = ops.Add(H);
        ops.Add(0x2000, w => w.Root = H);
        var controller = new WindowController(ops, OwnPid);

        var result = controller.ToggleTopmost(0x2000);

        Assert.Equal(OpStatus.Ok, result.Status);
        Assert.True((root.ExStyle & WindowStyles.WS_EX_TOPMOST) != 0);
        Assert.Equal(H, controller.TrackedWindows.Single().Hwnd);
        Assert.Same(controller.Find(H), controller.Find(0x2000));
    }

    [Fact]
    public void UnknownHandle_IsNotEligible()
    {
        var (controller, _, _) = Setup();

        Assert.Equal(OpStatus.NotEligible, controller.ToggleTopmost(0xDEAD).Status);
        Assert.Equal(OpStatus.NotEligible, controller.ToggleTopmost(0).Status);
    }

    [Fact]
    public void Forget_DropsTrackingWithoutTouchingTheWindow()
    {
        var (controller, ops, window) = Setup();
        controller.SetOpacity(H, 50);
        int calls = ops.Calls.Count;

        controller.Forget(H);

        Assert.Empty(controller.TrackedWindows);
        Assert.Equal(calls, ops.Calls.Count);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_LAYERED) != 0);
    }

    [Fact]
    public void Prune_DropsWindowsThatNoLongerExist()
    {
        var (controller, ops, _) = Setup();
        ops.Add(0x3000);
        controller.ToggleTopmost(H);
        controller.ToggleTopmost(0x3000);

        ops.Windows.Remove(H);

        Assert.Equal(1, controller.Prune());
        Assert.Equal(0x3000, controller.TrackedWindows.Single().Hwnd);
    }

    [Fact]
    public void RestoreAll_SurvivesWindowsThatVanished()
    {
        var (controller, ops, _) = Setup();
        ops.Add(0x3000);
        controller.ToggleTopmost(H);
        controller.ToggleTopmost(0x3000);
        ops.Windows.Remove(H);

        int restored = controller.RestoreAll();

        Assert.Equal(1, restored);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void Restore_OnUnmodifiedWindow_IsSkipped()
    {
        var (controller, _, _) = Setup();

        Assert.Equal(OpStatus.Skipped, controller.Restore(H).Status);
    }

    [Fact]
    public void Changed_FiresOnEveryStateChange()
    {
        var (controller, _, _) = Setup();
        int fired = 0;
        controller.Changed += () => fired++;

        controller.ToggleTopmost(H);
        controller.SetOpacity(H, 50);
        controller.RestoreAll();

        Assert.Equal(3, fired);
    }

    [Fact]
    public void TrackedWindow_ToStringSummarisesChanges()
    {
        var (controller, _, _) = Setup(w => w.Title = "Notes");
        controller.ToggleTopmost(H);
        controller.SetOpacity(H, 70);
        controller.SetClickThrough(H, true);

        Assert.Equal("Notes (pinned, 70%, click-through)", controller.Find(H)!.ToString());
    }

    /// <summary>Fake where SetLayeredWindowAttributes always fails, to test the rollback path.</summary>
    private sealed class FailingAlphaOps : IWindowOps
    {
        private readonly FakeWindowOps _inner = new();

        public Dictionary<nint, FakeWindowOps.FakeWindow> Windows => _inner.Windows;

        public int LastError { get; private set; }

        public FakeWindowOps.FakeWindow Add(nint hwnd) => _inner.Add(hwnd);

        public bool IsWindow(nint hwnd) => _inner.IsWindow(hwnd);

        public nint GetRootWindow(nint hwnd) => _inner.GetRootWindow(hwnd);

        public WindowSnapshot? TryGetSnapshot(nint hwnd) => _inner.TryGetSnapshot(hwnd);

        public uint GetExStyle(nint hwnd) => _inner.GetExStyle(hwnd);

        public bool SetExStyle(nint hwnd, uint exStyle)
        {
            bool ok = _inner.SetExStyle(hwnd, exStyle);
            LastError = _inner.LastError;
            return ok;
        }

        public bool SetTopmost(nint hwnd, bool topmost)
        {
            bool ok = _inner.SetTopmost(hwnd, topmost);
            LastError = _inner.LastError;
            return ok;
        }

        public bool SetLayeredAlpha(nint hwnd, byte alpha)
        {
            LastError = 1;
            return false;
        }

        public void Redraw(nint hwnd) => _inner.Redraw(hwnd);
    }
}

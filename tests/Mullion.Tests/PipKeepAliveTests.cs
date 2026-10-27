using Mullion.Core;

namespace Mullion.Tests;

public class PipKeepAliveTests
{
    private const uint WindowEdge = 0x00000100;
    private const uint LwaAlpha = 0x2;
    private const uint LwaColorKey = 0x1;

    [Fact]
    public void GhostExStyle_AddsLayeredAndTransparentAndKeepsTheRest()
    {
        uint ghost = PipKeepAlive.GhostExStyle(WindowEdge | WindowStyles.WS_EX_TOPMOST);

        Assert.Equal(WindowEdge | WindowStyles.WS_EX_TOPMOST | WindowStyles.WS_EX_LAYERED | WindowStyles.WS_EX_TRANSPARENT, ghost);
    }

    [Fact]
    public void RestoreExStyle_PutsBackOnlyManagedBits()
    {
        const uint acceptFiles = 0x10;
        uint original = WindowEdge;
        uint current = PipKeepAlive.GhostExStyle(original) | acceptFiles; // app added a bit meanwhile

        Assert.Equal(WindowEdge | acceptFiles, PipKeepAlive.RestoreExStyle(current, original));
    }

    [Fact]
    public void RestoreExStyle_KeepsOriginalLayeredBit()
    {
        uint original = WindowStyles.WS_EX_LAYERED;
        uint current = PipKeepAlive.GhostExStyle(original);

        Assert.Equal(WindowStyles.WS_EX_LAYERED, PipKeepAlive.RestoreExStyle(current, original));
    }

    [Fact]
    public void CanGhost_PlainWindow() => Assert.True(PipKeepAlive.CanGhost(WindowEdge, null));

    [Fact]
    public void CanGhost_ConstantAlphaLayeredWindow() =>
        Assert.True(PipKeepAlive.CanGhost(WindowStyles.WS_EX_LAYERED, LwaAlpha));

    [Fact]
    public void CanGhost_RefusesUpdateLayeredWindowApps() =>
        Assert.False(PipKeepAlive.CanGhost(WindowStyles.WS_EX_LAYERED, null));

    [Fact]
    public void CanGhost_RefusesColorKeyedWindows() =>
        Assert.False(PipKeepAlive.CanGhost(WindowStyles.WS_EX_LAYERED, LwaColorKey | LwaAlpha));

    [Fact]
    public void DefaultGhostAlpha_IsNearlyButNotFullyTransparent() =>
        Assert.Equal(1, PipKeepAlive.DefaultGhostAlpha);

    [Fact]
    public void Settings_KeepAwakeDefaultsOnAndRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "MullionTests", Guid.NewGuid().ToString("N"), "s.json");
        try
        {
            var store = new SettingsStore(path);
            Assert.True(store.Load().PipKeepSourceAwake);

            store.Save(new AppSettings { PipKeepSourceAwake = false });
            Assert.False(store.Load().PipKeepSourceAwake);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

public class WindowControllerSuspendTests
{
    private const nint H = 0x1000;

    [Fact]
    public void SuspendedWindow_RefusesOperationsAndLeavesStyleAlone()
    {
        var ops = new FakeWindowOps();
        var window = ops.Add(H);
        var controller = new WindowController(ops, 999);

        controller.Suspend(H);

        Assert.True(controller.IsSuspended(H));
        Assert.Equal(OpStatus.Skipped, controller.ToggleTopmost(H).Status);
        Assert.Equal(OpStatus.Skipped, controller.SetOpacity(H, 40).Status);
        Assert.Equal(OpStatus.Skipped, controller.ToggleClickThrough(H).Status);
        Assert.Equal(0u, window.ExStyle);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void SuspendedWindow_RefusesSingleRestoreButRestoreAllStillRuns()
    {
        var ops = new FakeWindowOps();
        var window = ops.Add(H);
        var controller = new WindowController(ops, 999);
        controller.ToggleTopmost(H);
        controller.Suspend(H);

        Assert.Equal(OpStatus.Skipped, controller.Restore(H).Status);
        Assert.True((window.ExStyle & WindowStyles.WS_EX_TOPMOST) != 0);

        Assert.Equal(1, controller.RestoreAll());
        Assert.Equal(0u, window.ExStyle);
    }

    [Fact]
    public void ChildOfSuspendedRoot_IsAlsoSuspended()
    {
        var ops = new FakeWindowOps();
        ops.Add(H);
        ops.Add(0x2000, w => w.Root = H);
        var controller = new WindowController(ops, 999);

        controller.Suspend(H);

        Assert.True(controller.IsSuspended(0x2000));
        Assert.Equal(OpStatus.Skipped, controller.ToggleTopmost(0x2000).Status);
    }

    [Fact]
    public void Resume_AllowsOperationsAgain()
    {
        var ops = new FakeWindowOps();
        ops.Add(H);
        var controller = new WindowController(ops, 999);
        controller.Suspend(H);

        controller.Resume(H);

        Assert.False(controller.IsSuspended(H));
        Assert.Equal(OpStatus.Ok, controller.ToggleTopmost(H).Status);
    }
}

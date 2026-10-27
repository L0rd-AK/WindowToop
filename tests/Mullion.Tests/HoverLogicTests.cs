using Mullion.Core;

namespace Mullion.Tests;

public class HoverLogicTests
{
    private static readonly PixelRect Window = new(100, 100, 900, 700);
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(500, 120, true)]
    [InlineData(899, 131, true)]
    [InlineData(500, 132, false)]
    [InlineData(99, 110, false)]
    [InlineData(900, 110, false)]
    [InlineData(500, 99, false)]
    public void IsInTitleStrip_UsesTopBandOfWindow(int x, int y, bool expected) =>
        Assert.Equal(expected, HoverLogic.IsInTitleStrip(Window, x, y, stripHeight: 32));

    [Fact]
    public void IsInTitleStrip_EmptyWindowOrStrip_IsFalse()
    {
        Assert.False(HoverLogic.IsInTitleStrip(default, 0, 0, 32));
        Assert.False(HoverLogic.IsInTitleStrip(Window, 500, 110, 0));
    }

    [Theory]
    [InlineData(false, false, false, HoverDecision.None)]
    [InlineData(false, true, false, HoverDecision.Show)]
    [InlineData(true, true, false, HoverDecision.KeepVisible)]
    [InlineData(true, false, true, HoverDecision.KeepVisible)]
    [InlineData(true, false, false, HoverDecision.ScheduleHide)]
    public void Decide_FollowsHoverStateMachine(bool visible, bool inStrip, bool overToolbar, HoverDecision expected) =>
        Assert.Equal(expected, HoverLogic.Decide(visible, inStrip, overToolbar));

    [Fact]
    public void PlaceToolbar_CentersOnTopEdge()
    {
        var (x, y) = HoverLogic.PlaceToolbar(Window, toolbarWidth: 200, toolbarHeight: 40, Monitor, topInset: 4);

        Assert.Equal(400, x);
        Assert.Equal(104, y);
    }

    [Fact]
    public void PlaceToolbar_ClampsToMonitorWorkArea()
    {
        var offscreen = new PixelRect(-300, -50, 100, 400);

        var (x, y) = HoverLogic.PlaceToolbar(offscreen, 200, 40, Monitor, 4);

        Assert.Equal(0, x);
        Assert.Equal(0, y);

        var farRight = new PixelRect(1800, 1000, 2400, 1300);
        (x, y) = HoverLogic.PlaceToolbar(farRight, 200, 40, Monitor, 4);

        Assert.Equal(1720, x);
        Assert.Equal(1000, y);
    }

    [Fact]
    public void PlaceToolbar_WithoutMonitor_DoesNotClamp()
    {
        var (x, y) = HoverLogic.PlaceToolbar(new PixelRect(-500, -500, -100, -100), 200, 40, default, 0);

        Assert.Equal(-400, x);
        Assert.Equal(-500, y);
    }

    [Fact]
    public void PixelRect_Basics()
    {
        var rect = PixelRect.FromSize(10, 20, 30, 40);

        Assert.Equal(40, rect.Right);
        Assert.Equal(60, rect.Bottom);
        Assert.Equal(30, rect.Width);
        Assert.Equal(40, rect.Height);
        Assert.True(rect.Contains(10, 20));
        Assert.False(rect.Contains(40, 20));
        Assert.False(rect.IsEmpty);
        Assert.True(new PixelRect(5, 5, 5, 10).IsEmpty);
    }
}

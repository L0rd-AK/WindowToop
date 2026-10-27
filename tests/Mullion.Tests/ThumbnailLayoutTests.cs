using Mullion.Core;

namespace Mullion.Tests;

public class ThumbnailLayoutTests
{
    [Fact]
    public void Fit_WideSourceIntoSquare_LetterboxesVertically()
    {
        var rect = ThumbnailLayout.Fit(1920, 1080, 400, 400);

        Assert.Equal(0, rect.Left);
        Assert.Equal(400, rect.Right);
        Assert.Equal(225, rect.Height);
        Assert.Equal(87, rect.Top);
    }

    [Fact]
    public void Fit_TallSourceIntoWide_PillarboxesHorizontally()
    {
        var rect = ThumbnailLayout.Fit(600, 1200, 800, 400);

        Assert.Equal(400, rect.Bottom - rect.Top);
        Assert.Equal(200, rect.Width);
        Assert.Equal(300, rect.Left);
    }

    [Fact]
    public void Fit_ExactAspect_FillsDestination()
    {
        var rect = ThumbnailLayout.Fit(160, 90, 320, 180);

        Assert.Equal(new PixelRect(0, 0, 320, 180), rect);
    }

    [Fact]
    public void Fit_DegenerateSource_FillsDestination() =>
        Assert.Equal(new PixelRect(0, 0, 300, 200), ThumbnailLayout.Fit(0, 0, 300, 200));

    [Fact]
    public void Fit_DegenerateDestination_IsEmpty() =>
        Assert.True(ThumbnailLayout.Fit(100, 100, 0, 50).IsEmpty);

    [Fact]
    public void InitialSize_IsAFractionOfWorkAreaWithMinimums()
    {
        var work = new PixelRect(0, 0, 1920, 1040);

        // A quarter of 1920x1040 is 480x260; the 16:9 source is height-limited, so 462x260.
        var (w, h) = ThumbnailLayout.InitialSize(1920, 1080, work);
        Assert.Equal(462, w);
        Assert.Equal(260, h);

        (w, h) = ThumbnailLayout.InitialSize(1920, 1080, new PixelRect(0, 0, 200, 200));
        Assert.Equal(160, w);
        Assert.Equal(90, h);
    }
}

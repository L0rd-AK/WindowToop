namespace Mullion.Core;

/// <summary>Letterboxes a DWM thumbnail of a given source size into a destination client area.</summary>
public static class ThumbnailLayout
{
    /// <summary>
    /// Returns the largest rectangle with the source aspect ratio that fits inside
    /// (0,0)-(destWidth,destHeight), centered. Degenerate inputs yield the full destination.
    /// </summary>
    public static PixelRect Fit(int sourceWidth, int sourceHeight, int destWidth, int destHeight)
    {
        if (destWidth <= 0 || destHeight <= 0)
        {
            return new PixelRect(0, 0, 0, 0);
        }

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return new PixelRect(0, 0, destWidth, destHeight);
        }

        double scale = Math.Min(destWidth / (double)sourceWidth, destHeight / (double)sourceHeight);
        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        int left = (destWidth - width) / 2;
        int top = (destHeight - height) / 2;
        return new PixelRect(left, top, left + width, top + height);
    }

    /// <summary>Picks an initial PiP window size: a fraction of the work area, keeping the source aspect ratio.</summary>
    public static (int Width, int Height) InitialSize(int sourceWidth, int sourceHeight, PixelRect workArea, double fraction = 0.25)
    {
        int maxWidth = Math.Max(160, (int)(workArea.Width * fraction));
        int maxHeight = Math.Max(90, (int)(workArea.Height * fraction));
        var fit = Fit(sourceWidth, sourceHeight, maxWidth, maxHeight);
        return (Math.Max(160, fit.Width), Math.Max(90, fit.Height));
    }
}

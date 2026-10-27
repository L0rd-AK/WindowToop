namespace Mullion.Core;

/// <summary>Opacity helpers shared by hotkeys, toolbar and controller.</summary>
public static class OpacityMath
{
    public const int Min = 10;
    public const int Max = 100;

    public static int Clamp(int percent) => Math.Clamp(percent, Min, Max);

    /// <summary>Applies a step and clamps. The result is snapped to a multiple of the step size.</summary>
    public static int Step(int current, int delta)
    {
        if (delta == 0)
        {
            return Clamp(current);
        }

        int step = Math.Abs(delta);
        int snapped = delta > 0
            ? (int)Math.Floor(current / (double)step) * step + step
            : (int)Math.Ceiling(current / (double)step) * step - step;
        return Clamp(snapped);
    }

    /// <summary>Converts 0-100 into the 0-255 alpha byte used by SetLayeredWindowAttributes.</summary>
    public static byte ToAlpha(int percent)
    {
        int clamped = Math.Clamp(percent, 0, Max);
        return (byte)Math.Round(clamped * 255.0 / Max, MidpointRounding.AwayFromZero);
    }

    public static int FromAlpha(byte alpha) => (int)Math.Round(alpha * 100.0 / 255.0, MidpointRounding.AwayFromZero);
}

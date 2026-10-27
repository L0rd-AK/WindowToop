using Mullion.Core;

namespace Mullion.Tests;

public class OpacityMathTests
{
    [Theory]
    [InlineData(-5, 10)]
    [InlineData(0, 10)]
    [InlineData(10, 10)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public void Clamp_KeepsValuesInRange(int input, int expected) =>
        Assert.Equal(expected, OpacityMath.Clamp(input));

    [Theory]
    [InlineData(100, -10, 90)]
    [InlineData(90, 10, 100)]
    [InlineData(100, 10, 100)]
    [InlineData(10, -10, 10)]
    [InlineData(15, -10, 10)]
    [InlineData(15, 10, 20)]
    [InlineData(95, 10, 100)]
    [InlineData(50, -25, 25)]
    [InlineData(50, 0, 50)]
    public void Step_SnapsToMultiplesAndClamps(int current, int delta, int expected) =>
        Assert.Equal(expected, OpacityMath.Step(current, delta));

    [Theory]
    [InlineData(100, 255)]
    [InlineData(50, 128)]
    [InlineData(10, 26)]
    [InlineData(0, 0)]
    public void ToAlpha_ScalesTo255(int percent, int alpha) =>
        Assert.Equal((byte)alpha, OpacityMath.ToAlpha(percent));

    [Fact]
    public void FromAlpha_InvertsToAlphaWithinRounding()
    {
        for (int percent = 10; percent <= 100; percent++)
        {
            int roundTrip = OpacityMath.FromAlpha(OpacityMath.ToAlpha(percent));
            Assert.InRange(roundTrip, percent - 1, percent + 1);
        }
    }
}

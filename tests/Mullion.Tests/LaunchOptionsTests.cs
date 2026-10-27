using Mullion.Core;

namespace Mullion.Tests;

public class LaunchOptionsTests
{
    [Fact]
    public void NoArguments_IsPlainTrayLaunch()
    {
        var options = LaunchOptions.Parse(Array.Empty<string>());

        Assert.False(options.ShowDebugWindow);
        Assert.False(options.SelfTest);
        Assert.Equal(0, options.SmokeDurationMs);
        Assert.Null(options.SettingsPath);
    }

    [Theory]
    [InlineData("--debug")]
    [InlineData("/debug")]
    [InlineData("--DEBUG")]
    public void Debug_SwitchForms(string arg) =>
        Assert.True(LaunchOptions.Parse(new[] { arg }).ShowDebugWindow);

    [Fact]
    public void SelfTest_TakesReportPath()
    {
        var options = LaunchOptions.Parse(new[] { "--selftest", @"C:\tmp\report.txt" });

        Assert.True(options.SelfTest);
        Assert.Equal(@"C:\tmp\report.txt", options.SelfTestReportPath);
    }

    [Fact]
    public void SelfTest_WithoutPath_UsesTempDefault()
    {
        var options = LaunchOptions.Parse(new[] { "--selftest", "--debug" });

        Assert.True(options.SelfTest);
        Assert.EndsWith("mullion-selftest.txt", options.SelfTestReportPath, StringComparison.Ordinal);
        Assert.True(options.ShowDebugWindow);
    }

    [Theory]
    [InlineData(new[] { "--smoke" }, 3000)]
    [InlineData(new[] { "--smoke", "4500" }, 4500)]
    [InlineData(new[] { "--smoke", "5" }, 100)]
    [InlineData(new[] { "--smoke", "abc" }, 3000)]
    public void Smoke_DurationParsingAndClamping(string[] args, int expected) =>
        Assert.Equal(expected, LaunchOptions.Parse(args).SmokeDurationMs);

    [Fact]
    public void Settings_OverridesPath()
    {
        var options = LaunchOptions.Parse(new[] { "--settings", @"D:\portable\mullion.json", "--debug" });

        Assert.Equal(@"D:\portable\mullion.json", options.SettingsPath);
        Assert.True(options.ShowDebugWindow);
    }

    [Fact]
    public void UnknownArguments_AreIgnored()
    {
        var options = LaunchOptions.Parse(new[] { "--whatever", "value", "--debug" });

        Assert.True(options.ShowDebugWindow);
        Assert.Null(options.SettingsPath);
    }
}

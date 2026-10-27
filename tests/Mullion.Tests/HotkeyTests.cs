using Mullion.Core;

namespace Mullion.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+T", HotkeyModifiers.Control | HotkeyModifiers.Alt, 'T')]
    [InlineData("ctrl + alt + t", HotkeyModifiers.Control | HotkeyModifiers.Alt, 'T')]
    [InlineData("Control+Shift+F5", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x74)]
    [InlineData("Win+Up", HotkeyModifiers.Win, 0x26)]
    [InlineData("Ctrl+Alt+Down", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x28)]
    [InlineData("Alt+Space", HotkeyModifiers.Alt, 0x20)]
    [InlineData("Ctrl+Num5", HotkeyModifiers.Control, 0x65)]
    [InlineData("F12", HotkeyModifiers.None, 0x7B)]
    [InlineData("Shift+9", HotkeyModifiers.Shift, '9')]
    public void Parse_AcceptsCommonForms(string text, HotkeyModifiers modifiers, int vk)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(modifiers, hotkey.Modifiers);
        Assert.Equal((uint)vk, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("Ctrl+Alt+T")]
    [InlineData("Ctrl+Alt+Up")]
    [InlineData("Ctrl+Shift+Win+F3")]
    [InlineData("Alt+PageDown")]
    [InlineData("F1")]
    public void ToString_RoundTrips(string text)
    {
        var hotkey = Hotkey.Parse(text);

        Assert.Equal(text, hotkey.ToString());
        Assert.Equal(hotkey, Hotkey.Parse(hotkey.ToString()));
    }

    [Theory]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl+Alt+T+U")]
    [InlineData("Ctrl+Bogus")]
    [InlineData("Ctrl+F99")]
    [InlineData("+")]
    public void Parse_RejectsMalformedInput(string text)
    {
        Assert.False(Hotkey.TryParse(text, out var hotkey));
        Assert.True(hotkey.IsEmpty);
        Assert.Throws<FormatException>(() => Hotkey.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_TreatsBlankAsUnbound(string? text)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.True(hotkey.IsEmpty);
        Assert.Equal(string.Empty, hotkey.ToString());
    }

    [Fact]
    public void IsUsable_RequiresModifierUnlessFunctionKey()
    {
        Assert.True(Hotkey.Parse("Ctrl+T").IsUsable);
        Assert.True(Hotkey.Parse("F7").IsUsable);
        Assert.False(Hotkey.Parse("T").IsUsable);
        Assert.False(Hotkey.None.IsUsable);
    }

    [Fact]
    public void ModifierValues_MatchWin32ModConstants()
    {
        Assert.Equal(1, (int)HotkeyModifiers.Alt);
        Assert.Equal(2, (int)HotkeyModifiers.Control);
        Assert.Equal(4, (int)HotkeyModifiers.Shift);
        Assert.Equal(8, (int)HotkeyModifiers.Win);
    }

    [Fact]
    public void VirtualKeys_UnknownCodeStillHasAName() =>
        Assert.Equal("VKE5", VirtualKeys.NameOf(0xE5));

    [Fact]
    public void HotkeySettings_DefaultsMatchSpec()
    {
        var settings = new HotkeySettings();

        Assert.Equal("Ctrl+Alt+T", settings.Get(HotkeyAction.ToggleTopmost));
        Assert.Equal("Ctrl+Alt+Up", settings.Get(HotkeyAction.OpacityUp));
        Assert.Equal("Ctrl+Alt+Down", settings.Get(HotkeyAction.OpacityDown));
        Assert.Equal("Ctrl+Alt+C", settings.Get(HotkeyAction.ToggleClickThrough));
        Assert.Equal("Ctrl+Alt+P", settings.Get(HotkeyAction.PictureInPicture));
        Assert.All(settings.Enumerate(), pair => Assert.True(pair.Hotkey.IsUsable));
    }

    [Fact]
    public void HotkeySettings_SetAndCloneAreIndependent()
    {
        var settings = new HotkeySettings();
        var copy = settings.Clone();

        copy.Set(HotkeyAction.ToggleTopmost, "Ctrl+Shift+Z");

        Assert.Equal("Ctrl+Alt+T", settings.ToggleTopmost);
        Assert.Equal("Ctrl+Shift+Z", copy.ToggleTopmost);
    }
}

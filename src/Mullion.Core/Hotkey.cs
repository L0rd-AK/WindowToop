using System.Text;

namespace Mullion.Core;

/// <summary>Modifier flags, numerically identical to the MOD_* values RegisterHotKey expects.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>Everything a hotkey can trigger.</summary>
public enum HotkeyAction
{
    ToggleTopmost,
    OpacityUp,
    OpacityDown,
    ToggleClickThrough,
    PictureInPicture,
}

/// <summary>A modifier set plus a virtual-key code, with a human-readable round-trippable form ("Ctrl+Alt+T").</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static readonly Hotkey None = default;

    public bool IsEmpty => VirtualKey == 0;

    /// <summary>A bare key without modifiers would hijack normal typing; only function keys are allowed alone.</summary>
    public bool IsUsable => !IsEmpty && (Modifiers != HotkeyModifiers.None || VirtualKeys.IsFunctionKey(VirtualKey));

    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            sb.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            sb.Append("Alt+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            sb.Append("Shift+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            sb.Append("Win+");
        }

        sb.Append(VirtualKeys.NameOf(VirtualKey));
        return sb.ToString();
    }

    public static Hotkey Parse(string text) =>
        TryParse(text, out var hotkey) ? hotkey : throw new FormatException($"'{text}' is not a valid hotkey.");

    /// <summary>Accepts forms like "Ctrl+Alt+T", "control + shift + F5", "Win+Up". Empty text parses as <see cref="None"/>.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var modifiers = HotkeyModifiers.None;
        uint key = 0;
        foreach (string rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    if (key != 0 || !VirtualKeys.TryFromName(rawPart, out key))
                    {
                        hotkey = None;
                        return false;
                    }

                    break;
            }
        }

        if (key == 0)
        {
            hotkey = None;
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }
}

/// <summary>Name table for the virtual keys users can bind. Deliberately independent of WPF's Key enum.</summary>
public static class VirtualKeys
{
    private static readonly Dictionary<string, uint> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08, ["Back"] = 0x08,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D, ["Return"] = 0x0D,
        ["Pause"] = 0x13,
        ["CapsLock"] = 0x14,
        ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["PgUp"] = 0x21,
        ["PageDown"] = 0x22, ["PgDn"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["PrintScreen"] = 0x2C,
        ["Insert"] = 0x2D, ["Ins"] = 0x2D,
        ["Delete"] = 0x2E, ["Del"] = 0x2E,
        ["Num0"] = 0x60, ["Num1"] = 0x61, ["Num2"] = 0x62, ["Num3"] = 0x63, ["Num4"] = 0x64,
        ["Num5"] = 0x65, ["Num6"] = 0x66, ["Num7"] = 0x67, ["Num8"] = 0x68, ["Num9"] = 0x69,
        ["NumMultiply"] = 0x6A, ["NumAdd"] = 0x6B, ["NumSubtract"] = 0x6D, ["NumDecimal"] = 0x6E, ["NumDivide"] = 0x6F,
        ["NumLock"] = 0x90, ["ScrollLock"] = 0x91,
        ["Semicolon"] = 0xBA, ["Plus"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD, ["Period"] = 0xBE,
        ["Slash"] = 0xBF, ["Tilde"] = 0xC0, ["OpenBracket"] = 0xDB, ["Backslash"] = 0xDC, ["CloseBracket"] = 0xDD, ["Quote"] = 0xDE,
    };

    private static readonly Dictionary<uint, string> ByCode = BuildReverse();

    public static bool IsFunctionKey(uint vk) => vk is >= 0x70 and <= 0x87;

    public static bool TryFromName(string name, out uint vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        name = name.Trim();
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                vk = c;
                return true;
            }

            return false;
        }

        if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        return ByName.TryGetValue(name, out vk);
    }

    public static string NameOf(uint vk)
    {
        if (vk is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
        {
            return ((char)vk).ToString();
        }

        if (IsFunctionKey(vk))
        {
            return "F" + (vk - 0x70 + 1);
        }

        return ByCode.TryGetValue(vk, out var name) ? name : $"VK{vk:X2}";
    }

    private static Dictionary<uint, string> BuildReverse()
    {
        var reverse = new Dictionary<uint, string>();
        foreach (var (name, code) in ByName)
        {
            // First alias wins, so the canonical spelling is listed first in the table above.
            reverse.TryAdd(code, name);
        }

        return reverse;
    }
}

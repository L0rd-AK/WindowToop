using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mullion.Core;

/// <summary>Hotkey bindings in their textual form so the JSON file stays hand-editable.</summary>
public sealed class HotkeySettings
{
    public string ToggleTopmost { get; set; } = "Ctrl+Alt+T";

    public string OpacityUp { get; set; } = "Ctrl+Alt+Up";

    public string OpacityDown { get; set; } = "Ctrl+Alt+Down";

    public string ToggleClickThrough { get; set; } = "Ctrl+Alt+C";

    public string PictureInPicture { get; set; } = "Ctrl+Alt+P";

    public string Get(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleTopmost => ToggleTopmost,
        HotkeyAction.OpacityUp => OpacityUp,
        HotkeyAction.OpacityDown => OpacityDown,
        HotkeyAction.ToggleClickThrough => ToggleClickThrough,
        HotkeyAction.PictureInPicture => PictureInPicture,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public void Set(HotkeyAction action, string text)
    {
        switch (action)
        {
            case HotkeyAction.ToggleTopmost:
                ToggleTopmost = text;
                break;
            case HotkeyAction.OpacityUp:
                OpacityUp = text;
                break;
            case HotkeyAction.OpacityDown:
                OpacityDown = text;
                break;
            case HotkeyAction.ToggleClickThrough:
                ToggleClickThrough = text;
                break;
            case HotkeyAction.PictureInPicture:
                PictureInPicture = text;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    /// <summary>Parsed bindings for every action. Unparseable or empty entries yield <see cref="Hotkey.None"/>.</summary>
    public IEnumerable<(HotkeyAction Action, Hotkey Hotkey)> Enumerate()
    {
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            Hotkey.TryParse(Get(action), out var hotkey);
            yield return (action, hotkey);
        }
    }

    public HotkeySettings Clone() => (HotkeySettings)MemberwiseClone();
}

/// <summary>Everything persisted to settings.json.</summary>
public sealed class AppSettings
{
    public const int MinOpacityStep = 5;
    public const int MaxOpacityStep = 50;

    /// <summary>Show the floating toolbar when hovering a title bar.</summary>
    public bool ToolbarEnabled { get; set; } = true;

    /// <summary>Percent change per opacity hotkey press.</summary>
    public int OpacityStep { get; set; } = 10;

    /// <summary>How long the mouse may be away from the toolbar/title bar before the toolbar hides.</summary>
    public int ToolbarHideDelayMs { get; set; } = 600;

    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>Clamps out-of-range values after loading a hand-edited file.</summary>
    public void Normalize()
    {
        OpacityStep = Math.Clamp(OpacityStep, MinOpacityStep, MaxOpacityStep);
        ToolbarHideDelayMs = Math.Clamp(ToolbarHideDelayMs, 100, 5000);
        Hotkeys ??= new HotkeySettings();
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Hotkeys = Hotkeys.Clone();
        return copy;
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Missing or corrupt files fall back to defaults.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Local config file, never served to a browser: keep "Ctrl+Alt+T" readable instead of "Ctrl+Alt+T".
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SettingsStore(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Path { get; }

    /// <summary>%APPDATA%\Mullion\settings.json</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mullion",
        "settings.json");

    /// <summary>Set when the last <see cref="Load"/> had to discard an unreadable file.</summary>
    public string? LastLoadError { get; private set; }

    public AppSettings Load()
    {
        LastLoadError = null;
        try
        {
            if (!File.Exists(Path))
            {
                return new AppSettings();
            }

            using var stream = File.OpenRead(Path);
            var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LastLoadError = ex.Message;
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a sibling temp file first so a crash mid-write never leaves a truncated settings file.
        string temp = Path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, settings, JsonOptions);
        }

        File.Move(temp, Path, overwrite: true);
    }
}

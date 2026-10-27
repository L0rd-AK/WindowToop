using Mullion.Core;

namespace Mullion.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MullionTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsDefaults()
    {
        var store = new SettingsStore(SettingsPath);

        var settings = store.Load();

        Assert.True(settings.ToolbarEnabled);
        Assert.Equal(10, settings.OpacityStep);
        Assert.Equal("Ctrl+Alt+T", settings.Hotkeys.ToggleTopmost);
        Assert.Null(store.LastLoadError);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = new AppSettings { ToolbarEnabled = false, OpacityStep = 25, ToolbarHideDelayMs = 900 };
        settings.Hotkeys.Set(HotkeyAction.OpacityUp, "Ctrl+Shift+PageUp");

        store.Save(settings);
        var loaded = store.Load();

        Assert.False(loaded.ToolbarEnabled);
        Assert.Equal(25, loaded.OpacityStep);
        Assert.Equal(900, loaded.ToolbarHideDelayMs);
        Assert.Equal("Ctrl+Shift+PageUp", loaded.Hotkeys.OpacityUp);
        Assert.Equal("Ctrl+Alt+Down", loaded.Hotkeys.OpacityDown);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Save_WritesCamelCaseJson()
    {
        var store = new SettingsStore(SettingsPath);

        store.Save(new AppSettings());
        string json = File.ReadAllText(SettingsPath);

        Assert.Contains("\"toolbarEnabled\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hotkeys\"", json, StringComparison.Ordinal);
        Assert.Contains("\"toggleTopmost\": \"Ctrl+Alt+T\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaultsAndReportsError()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ this is not json");
        var store = new SettingsStore(SettingsPath);

        var settings = store.Load();

        Assert.Equal(10, settings.OpacityStep);
        Assert.NotNull(store.LastLoadError);
    }

    [Fact]
    public void Load_ClampsOutOfRangeValuesAndFillsMissingSections()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ \"opacityStep\": 500, \"toolbarHideDelayMs\": 1, \"hotkeys\": null }");
        var store = new SettingsStore(SettingsPath);

        var settings = store.Load();

        Assert.Equal(AppSettings.MaxOpacityStep, settings.OpacityStep);
        Assert.Equal(100, settings.ToolbarHideDelayMs);
        Assert.NotNull(settings.Hotkeys);
        Assert.Equal("Ctrl+Alt+C", settings.Hotkeys.ToggleClickThrough);
    }

    [Fact]
    public void Load_IgnoresUnknownPropertiesAndComments()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ // comment\n \"futureSetting\": true, \"toolbarEnabled\": false, }");
        var store = new SettingsStore(SettingsPath);

        var settings = store.Load();

        Assert.False(settings.ToolbarEnabled);
        Assert.Null(store.LastLoadError);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var settings = new AppSettings();
        var copy = settings.Clone();

        copy.Hotkeys.ToggleTopmost = "Ctrl+Alt+Q";
        copy.OpacityStep = 5;

        Assert.Equal("Ctrl+Alt+T", settings.Hotkeys.ToggleTopmost);
        Assert.Equal(10, settings.OpacityStep);
    }

    [Fact]
    public void DefaultPath_IsUnderAppData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(appData, SettingsStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Mullion", "settings.json"), SettingsStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
    }
}

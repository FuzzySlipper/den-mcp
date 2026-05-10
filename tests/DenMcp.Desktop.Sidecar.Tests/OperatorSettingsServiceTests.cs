using System.Text.Json;
using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class OperatorSettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsNormalizedSettingsWithCamelCaseStorageShape()
    {
        var path = TempSettingsPath();
        var service = Service(path, "unused-source");

        var saved = service.Save(new OperatorSettings
        {
            DenBaseUrl = " http://localhost:5199/ ",
            SourceInstanceId = "stable-source",
            SourceDisplayName = "  Desk  ",
            PollIntervalSeconds = 2,
            MaxChangedFiles = 10_000,
        });
        var loaded = service.Load();
        var json = File.ReadAllText(path);

        Assert.Equal("http://localhost:5199", saved.DenBaseUrl);
        Assert.Equal(saved, loaded);
        Assert.Equal("stable-source", loaded.SourceInstanceId);
        Assert.Equal("Desk", loaded.SourceDisplayName);
        Assert.Equal(OperatorSettings.MinPollIntervalSeconds, loaded.PollIntervalSeconds);
        Assert.Equal(OperatorSettings.MaxChangedFilesLimit, loaded.MaxChangedFiles);
        Assert.True(loaded.IncludeHiddenSpaces);
        Assert.True(loaded.IncludeArchivedSpaces);
        Assert.Contains("\"denBaseUrl\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceInstanceId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"includeHiddenSpaces\"", json, StringComparison.Ordinal);
        Assert.Contains("\"includeArchivedSpaces\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("den_base_url", json, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Load_MissingFileFallsBackToDefaultsAndPersistsStableSourceInstanceId()
    {
        var path = TempSettingsPath();
        var service = Service(path, "den-desktop-generated-once");

        var first = service.Load();
        var second = service.Load();

        Assert.True(File.Exists(path));
        Assert.Equal(OperatorSettings.DefaultDenBaseUrl, first.DenBaseUrl);
        Assert.Equal("den-desktop-generated-once", first.SourceInstanceId);
        Assert.Equal(OperatorSettings.DefaultSourceDisplayName, first.SourceDisplayName);
        Assert.True(first.IncludeHiddenSpaces);
        Assert.True(first.IncludeArchivedSpaces);
        Assert.Equal(first.SourceInstanceId, second.SourceInstanceId);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Load_MalformedFileFallsBackToNormalizedDefaultsWithoutThrowing()
    {
        var path = TempSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not valid json");
        var service = Service(path, "den-desktop-default-after-malformed");

        var settings = service.Load();

        Assert.Equal(OperatorSettings.DefaultDenBaseUrl, settings.DenBaseUrl);
        Assert.Equal("den-desktop-default-after-malformed", settings.SourceInstanceId);
        Assert.Equal(OperatorSettings.DefaultSourceDisplayName, settings.SourceDisplayName);
        Assert.Equal(OperatorSettings.DefaultPollIntervalSeconds, settings.PollIntervalSeconds);
        Assert.Equal(OperatorSettings.DefaultMaxChangedFiles, settings.MaxChangedFiles);
        Assert.True(settings.IncludeHiddenSpaces);
        Assert.True(settings.IncludeArchivedSpaces);
    }

    [Fact]
    public void Load_ExistingSettingsWithoutVisibilityPolicyUsesSafeDefaults()
    {
        var path = TempSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "denBaseUrl": "http://den.test",
              "sourceInstanceId": "stable-source",
              "sourceDisplayName": "Desk",
              "pollIntervalSeconds": 30,
              "maxChangedFiles": 200
            }
            """);
        var service = Service(path, "unused-source");

        var settings = service.Load();

        Assert.Equal("http://den.test", settings.DenBaseUrl);
        Assert.True(settings.IncludeHiddenSpaces);
        Assert.True(settings.IncludeArchivedSpaces);
    }

    [Fact]
    public void Normalize_TrimsDefaultsRegeneratesBlankSourceAndClampsBounds()
    {
        var normalized = new OperatorSettings
        {
            DenBaseUrl = "   /",
            SourceInstanceId = "   ",
            SourceDisplayName = "   ",
            PollIntervalSeconds = 99_999,
            MaxChangedFiles = 1,
        }.Normalized(() => "den-desktop-regenerated");

        Assert.Equal(OperatorSettings.DefaultDenBaseUrl, normalized.DenBaseUrl);
        Assert.Equal("den-desktop-regenerated", normalized.SourceInstanceId);
        Assert.Null(normalized.SourceDisplayName);
        Assert.Equal(OperatorSettings.MaxPollIntervalSeconds, normalized.PollIntervalSeconds);
        Assert.Equal(OperatorSettings.MinChangedFiles, normalized.MaxChangedFiles);
        Assert.True(normalized.IncludeHiddenSpaces);
        Assert.True(normalized.IncludeArchivedSpaces);
    }

    [Fact]
    public void SaveRequest_MergesOntoCurrentSettingsAndPreservesSourceInstanceId()
    {
        var path = TempSettingsPath();
        var service = Service(path, "den-desktop-initial");
        service.Save(new OperatorSettings
        {
            DenBaseUrl = "http://old",
            SourceInstanceId = "stable-source",
            SourceDisplayName = "Old",
            PollIntervalSeconds = 45,
            MaxChangedFiles = 300,
            IncludeHiddenSpaces = false,
            IncludeArchivedSpaces = true,
        });

        var saved = service.Save(new SaveOperatorSettingsRequest
        {
            DenBaseUrl = " http://new/ ",
            SourceDisplayName = "   ",
            MaxChangedFiles = 4_000,
            IncludeArchivedSpaces = false,
        });

        Assert.Equal("stable-source", saved.SourceInstanceId);
        Assert.Equal("http://new", saved.DenBaseUrl);
        Assert.Null(saved.SourceDisplayName);
        Assert.Equal(45, saved.PollIntervalSeconds);
        Assert.Equal(OperatorSettings.MaxChangedFilesLimit, saved.MaxChangedFiles);
        Assert.False(saved.IncludeHiddenSpaces);
        Assert.False(saved.IncludeArchivedSpaces);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(document.RootElement.TryGetProperty("sourceDisplayName", out var sourceDisplayName));
        Assert.Equal(JsonValueKind.Null, sourceDisplayName.ValueKind);
    }

    [Fact]
    public void DefaultSettingsPath_UsesWrapperIndependentConfigRoot()
    {
        var path = OperatorSettingsStorage.DefaultSettingsPath("/home/tester");

        Assert.Equal(Path.GetFullPath("/home/tester/.config/den-desktop/operator-settings.json"), path);
        Assert.DoesNotContain(".den-mcp", path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("electron", path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tauri", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultAppearanceSettingsPath_UsesSameConfigRootWithAppearanceFileName()
    {
        var path = OperatorSettingsStorage.DefaultAppearanceSettingsPath("/home/tester");

        Assert.Equal(Path.GetFullPath("/home/tester/.config/den-desktop/appearance-settings.json"), path);
        Assert.DoesNotContain("operator-settings", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppearanceSettingsPath_ForPathNestedSettingsPathUsesSiblingAppearanceFile()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "den-mcp-settings-tests", Guid.NewGuid().ToString("N"), "custom-settings.json");
        var storage = OperatorSettingsStorage.ForPath(settingsPath);

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, OperatorSettingsStorage.AppearanceSettingsFileName),
            storage.AppearanceSettingsPath);
    }

    [Fact]
    public void AppearanceSettingsPath_ForPathBareFileNameUsesResolvedCurrentDirectory()
    {
        var storage = OperatorSettingsStorage.ForPath("settings.json");

        Assert.Equal(
            Path.Combine(Directory.GetCurrentDirectory(), OperatorSettingsStorage.AppearanceSettingsFileName),
            storage.AppearanceSettingsPath);
    }

    [Fact]
    public void AppearanceSettingsPath_DirectRelativeSettingsPathIsResolvedBeforeDerivingSiblingPath()
    {
        var storage = new OperatorSettingsStorage { SettingsPath = "settings.json" };

        Assert.Equal(
            Path.Combine(Directory.GetCurrentDirectory(), OperatorSettingsStorage.AppearanceSettingsFileName),
            storage.AppearanceSettingsPath);
    }

    [Fact]
    public void LoadAppearance_MissingFileFallsBackToDefaultsAndPersistsFile()
    {
        var path = TempSettingsPath();
        var service = Service(path);
        var appearancePath = service.AppearanceSettingsPath;

        var first = service.LoadAppearance();
        var second = service.LoadAppearance();

        Assert.True(File.Exists(appearancePath));
        Assert.Equal(OperatorAppearanceSettings.DefaultTheme, first.Theme);
        Assert.Equal(OperatorAppearanceSettings.DefaultAccent, first.Accent);
        Assert.Equal(OperatorAppearanceSettings.DefaultDensity, first.Density);
        Assert.Equal(OperatorAppearanceSettings.DefaultBodyFont, first.BodyFont);
        Assert.Equal(OperatorAppearanceSettings.DefaultRailMode, first.RailMode);
        Assert.Equal(OperatorAppearanceSettings.DefaultConsoleMode, first.ConsoleMode);
        Assert.Equal(OperatorAppearanceSettings.DefaultActiveTab, first.ActiveTab);
        Assert.Equal(first, second);
    }

    [Fact]
    public void SaveAppearance_RoundTripsNormalizedValues()
    {
        var path = TempSettingsPath();
        var service = Service(path);

        var saved = service.SaveAppearance(new OperatorAppearanceSettings
        {
            Theme = "graphite-dark",
            Accent = "violet",
            Density = "compact",
            BodyFont = "mono",
            RailMode = "collapsed",
            ConsoleMode = "half",
            ActiveTab = "git",
        });
        var loaded = service.LoadAppearance();

        Assert.Equal(saved, loaded);
        Assert.Equal("graphite-dark", loaded.Theme);
        Assert.Equal("violet", loaded.Accent);
        Assert.Equal("compact", loaded.Density);
        Assert.Equal("mono", loaded.BodyFont);
        Assert.Equal("collapsed", loaded.RailMode);
        Assert.Equal("half", loaded.ConsoleMode);
        Assert.Equal("git", loaded.ActiveTab);
    }

    [Fact]
    public void SaveAppearance_RoundTripsSnakeCaseFilePath()
    {
        var path = TempSettingsPath();
        var service = Service(path);

        service.SaveAppearance(new OperatorAppearanceSettings());
        var appearancePath = service.AppearanceSettingsPath;

        Assert.True(File.Exists(appearancePath));
        var json = File.ReadAllText(appearancePath);
        Assert.Contains("\"theme\"", json, StringComparison.Ordinal);
        Assert.Contains("\"accent\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("theme_name", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAppearance_MalformedFileFallsBackToDefaultsLikeOperatorSettingsAndLeavesFileForInspection()
    {
        var path = TempSettingsPath();
        var service = Service(path);
        var appearancePath = service.AppearanceSettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(appearancePath)!);
        File.WriteAllText(appearancePath, "{not valid json");

        var settings = service.LoadAppearance();

        Assert.Equal(OperatorAppearanceSettings.DefaultTheme, settings.Theme);
        Assert.Equal(OperatorAppearanceSettings.DefaultDensity, settings.Density);
        Assert.Equal("{not valid json", File.ReadAllText(appearancePath));
    }

    [Fact]
    public void LoadAppearanceFull_MissingFileReturnsDefaultsWithoutRecoveryFlag()
    {
        var path = TempSettingsPath();
        var service = Service(path);

        var result = service.LoadAppearanceFull();

        Assert.False(result.RecoveredFromMalformed);
        Assert.Equal(OperatorAppearanceSettings.DefaultTheme, result.Settings.Theme);
    }

    [Fact]
    public void LoadAppearanceFull_ValidFileReturnsLoadedSettingsWithoutRecoveryFlag()
    {
        var path = TempSettingsPath();
        var service = Service(path);
        service.SaveAppearance(new OperatorAppearanceSettings { Theme = "graphite-dark", Accent = "cyan" });

        var result = service.LoadAppearanceFull();

        Assert.False(result.RecoveredFromMalformed);
        Assert.Equal("graphite-dark", result.Settings.Theme);
        Assert.Equal("cyan", result.Settings.Accent);
    }

    [Fact]
    public void LoadAppearanceFull_MalformedFileReturnsDefaultsWithRecoveryFlagAndPreservesFile()
    {
        var path = TempSettingsPath();
        var service = Service(path);
        var appearancePath = service.AppearanceSettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(appearancePath)!);
        File.WriteAllText(appearancePath, "{not valid json");

        var result = service.LoadAppearanceFull();

        Assert.True(result.RecoveredFromMalformed);
        Assert.Equal(OperatorAppearanceSettings.DefaultTheme, result.Settings.Theme);
        Assert.Equal("{not valid json", File.ReadAllText(appearancePath));
    }

    [Fact]
    public void SaveAppearanceSettingsRequest_MergesPartialUpdate()
    {
        var path = TempSettingsPath();
        var service = Service(path);

        // First save full set
        service.SaveAppearance(new OperatorAppearanceSettings
        {
            Theme = "graphite-dark",
            Accent = "violet",
            Density = "compact",
            BodyFont = "mono",
            RailMode = "collapsed",
            ConsoleMode = "full",
            ActiveTab = "settings",
        });

        // Partial update: only theme and accent
        var merged = service.SaveAppearance(new SaveOperatorAppearanceSettingsRequest
        {
            Theme = "amber-dark",
            Accent = "cyan",
        });

        Assert.Equal("amber-dark", merged.Theme);
        Assert.Equal("cyan", merged.Accent);
        Assert.Equal("compact", merged.Density);   // Preserved from prior save
        Assert.Equal("mono", merged.BodyFont);     // Preserved
        Assert.Equal("collapsed", merged.RailMode); // Preserved
        Assert.Equal("full", merged.ConsoleMode);   // Preserved
        Assert.Equal("settings", merged.ActiveTab); // Preserved
    }

    [Fact]
    public void SaveAppearance_NormalizesInvalidChoicesToDefaults()
    {
        var path = TempSettingsPath();
        var service = Service(path);

        var saved = service.SaveAppearance(new OperatorAppearanceSettings
        {
            Theme = "neon-rainbow",
            Accent = "pink",
            Density = "ultra",
            BodyFont = "serif",
            RailMode = "floating",
            ConsoleMode = "maximized",
            ActiveTab = "unknown",
        });

        Assert.Equal(OperatorAppearanceSettings.DefaultTheme, saved.Theme);
        Assert.Equal(OperatorAppearanceSettings.DefaultAccent, saved.Accent);
        Assert.Equal(OperatorAppearanceSettings.DefaultDensity, saved.Density);
        Assert.Equal(OperatorAppearanceSettings.DefaultBodyFont, saved.BodyFont);
        Assert.Equal(OperatorAppearanceSettings.DefaultRailMode, saved.RailMode);
        Assert.Equal(OperatorAppearanceSettings.DefaultConsoleMode, saved.ConsoleMode);
        Assert.Equal(OperatorAppearanceSettings.DefaultActiveTab, saved.ActiveTab);
    }

    [Fact]
    public void ExistingOperatorSettingsContinueToWorkAfterAppearanceSettings()
    {
        var path = TempSettingsPath();
        var service = Service(path);

        // Save operator connection settings
        var opSaved = service.Save(new OperatorSettings
        {
            DenBaseUrl = "http://example.com:5199",
            SourceInstanceId = "test-source",
            PollIntervalSeconds = 60,
            MaxChangedFiles = 500,
        });

        // Save appearance settings
        service.SaveAppearance(new OperatorAppearanceSettings
        {
            Theme = "graphite-dark",
            Accent = "green",
            Density = "spacious",
        });

        // Verify both are independently preserved
        var opLoaded = service.Load();
        var appearanceLoaded = service.LoadAppearance();

        Assert.Equal("http://example.com:5199", opLoaded.DenBaseUrl);
        Assert.Equal(60, opLoaded.PollIntervalSeconds);
        Assert.Equal("graphite-dark", appearanceLoaded.Theme);
        Assert.Equal("green", appearanceLoaded.Accent);
        Assert.Equal("spacious", appearanceLoaded.Density);
        Assert.Equal(OperatorAppearanceSettings.DefaultBodyFont, appearanceLoaded.BodyFont);
    }

    private static OperatorSettingsService Service(string path, string generatedSourceInstanceId)
    {
        return new OperatorSettingsService(
            OperatorSettingsStorage.ForPath(path),
            () => generatedSourceInstanceId);
    }

    private static OperatorSettingsService Service(string path)
    {
        return new OperatorSettingsService(
            OperatorSettingsStorage.ForPath(path));
    }

    private static string TempSettingsPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "den-mcp-settings-tests",
            Guid.NewGuid().ToString("N"),
            OperatorSettingsStorage.SettingsFileName);
    }
}

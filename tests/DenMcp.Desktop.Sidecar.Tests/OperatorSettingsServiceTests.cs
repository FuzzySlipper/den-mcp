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
        Assert.Contains("\"denBaseUrl\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceInstanceId\"", json, StringComparison.Ordinal);
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
        });

        var saved = service.Save(new SaveOperatorSettingsRequest
        {
            DenBaseUrl = " http://new/ ",
            SourceDisplayName = "   ",
            MaxChangedFiles = 4_000,
        });

        Assert.Equal("stable-source", saved.SourceInstanceId);
        Assert.Equal("http://new", saved.DenBaseUrl);
        Assert.Null(saved.SourceDisplayName);
        Assert.Equal(45, saved.PollIntervalSeconds);
        Assert.Equal(OperatorSettings.MaxChangedFilesLimit, saved.MaxChangedFiles);

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

    private static OperatorSettingsService Service(string path, string generatedSourceInstanceId)
    {
        return new OperatorSettingsService(
            OperatorSettingsStorage.ForPath(path),
            () => generatedSourceInstanceId);
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

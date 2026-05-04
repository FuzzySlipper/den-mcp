using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

public sealed record OperatorSettingsStorage
{
    public const string SettingsFileName = "operator-settings.json";
    public const string AppearanceSettingsFileName = "appearance-settings.json";

    public required string SettingsPath { get; init; }

    public string AppearanceSettingsPath
    {
        get
        {
            var settingsPath = Path.GetFullPath(SettingsPath);
            var directory = Path.GetDirectoryName(settingsPath);
            return !string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(directory, AppearanceSettingsFileName)
                : DefaultAppearanceSettingsPath();
        }
    }

    public static OperatorSettingsStorage ForPath(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        return new OperatorSettingsStorage { SettingsPath = Path.GetFullPath(settingsPath) };
    }

    public static OperatorSettingsStorage CreateDefault()
    {
        return ForPath(DefaultSettingsPath());
    }

    /// <summary>
    /// Returns the standalone default settings path (~/.config/den-desktop/).
    /// This intentionally diverges from the bridge-runtime path (~/.den-mcp/desktop/)
    /// configured via <see cref="DesktopSidecarOptions.ConfigPath"/> because the
    /// bridge always provides an explicit path through DI. Standalone usage or
    /// tests that construct <see cref="OperatorSettingsService"/> without arguments
    /// use this default. See review finding R1000-2.
    /// </summary>
    public static string DefaultSettingsPath(string? homeDirectory = null)
    {
        var home = string.IsNullOrWhiteSpace(homeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : homeDirectory;

        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("HOME");
        }

        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException("Unable to resolve the current user's home directory for Den Desktop settings.");
        }

        return Path.GetFullPath(Path.Combine(home, ".config", "den-desktop", SettingsFileName));
    }

    public static string DefaultAppearanceSettingsPath(string? homeDirectory = null)
    {
        var home = string.IsNullOrWhiteSpace(homeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : homeDirectory;

        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("HOME");
        }

        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException("Unable to resolve the current user's home directory for Den Desktop appearance settings.");
        }

        return Path.GetFullPath(Path.Combine(home, ".config", "den-desktop", AppearanceSettingsFileName));
    }
}

public sealed class OperatorSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    private readonly Func<string> _sourceInstanceIdFactory;

    public OperatorSettingsService(OperatorSettingsStorage? storage = null, Func<string>? sourceInstanceIdFactory = null)
    {
        Storage = storage ?? OperatorSettingsStorage.CreateDefault();
        _sourceInstanceIdFactory = sourceInstanceIdFactory ?? (() => OperatorSettings.CreateSourceInstanceId());
    }

    public OperatorSettingsStorage Storage { get; }

    public string SettingsPath => Storage.SettingsPath;

    public OperatorSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            var defaults = CreateDefaultSettings();
            TrySaveDefault(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<OperatorSettings>(json, JsonOptions);
            return (settings ?? CreateDefaultSettings()).Normalized(_sourceInstanceIdFactory);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return CreateDefaultSettings();
        }
    }

    public OperatorSettings Save(OperatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings.Normalized(_sourceInstanceIdFactory);
        WriteSettings(normalized);
        return normalized;
    }

    public OperatorSettings Save(SaveOperatorSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var current = Load();
        var next = OperatorSettings.FromSaveRequest(current, request, _sourceInstanceIdFactory);
        WriteSettings(next);
        return next;
    }

    private OperatorSettings CreateDefaultSettings()
    {
        return OperatorSettings.CreateDefault(_sourceInstanceIdFactory).Normalized(_sourceInstanceIdFactory);
    }

    private void TrySaveDefault(OperatorSettings settings)
    {
        try
        {
            WriteSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Loading settings should still succeed with defaults even when the config root is not writable.
        }
    }

    private void WriteSettings(OperatorSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    // ── Appearance settings ────────────────────────────────────────────────

    public string AppearanceSettingsPath => Storage.AppearanceSettingsPath;

    public OperatorAppearanceSettings LoadAppearance()
    {
        return LoadAppearanceFull().Settings;
    }

    /// <summary>
    /// Loads appearance settings with recovery metadata.
    /// When the stored file is malformed or unreadable, returns defaults with
    /// <see cref="LoadAppearanceResult.RecoveredFromMalformed"/> set to <c>true</c>.
    /// The corrupt file is preserved on disk for inspection.
    /// </summary>
    public LoadAppearanceResult LoadAppearanceFull()
    {
        var path = AppearanceSettingsPath;
        if (!File.Exists(path))
        {
            var defaults = OperatorAppearanceSettings.CreateDefault();
            TrySaveDefaultAppearance(defaults);
            return new LoadAppearanceResult { Settings = defaults, RecoveredFromMalformed = false };
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<OperatorAppearanceSettings>(json, JsonOptions);
            return new LoadAppearanceResult
            {
                Settings = (settings ?? OperatorAppearanceSettings.CreateDefault()).Normalized(),
                RecoveredFromMalformed = false,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Match Load(): malformed/unreadable settings recover silently to defaults so a bad
            // local settings file cannot prevent Den Desktop from starting.
            return new LoadAppearanceResult
            {
                Settings = OperatorAppearanceSettings.CreateDefault(),
                RecoveredFromMalformed = true,
            };
        }
    }

    public OperatorAppearanceSettings SaveAppearance(OperatorAppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalized();
        WriteAppearanceSettings(normalized);
        return normalized;
    }

    public OperatorAppearanceSettings SaveAppearance(SaveOperatorAppearanceSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = LoadAppearance();
        var next = request.MergeInto(current);
        WriteAppearanceSettings(next);
        return next;
    }

    private void TrySaveDefaultAppearance(OperatorAppearanceSettings settings)
    {
        try
        {
            WriteAppearanceSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Loading appearance should still succeed with defaults when the config root is not writable.
        }
    }

    private void WriteAppearanceSettings(OperatorAppearanceSettings settings)
    {
        var path = AppearanceSettingsPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>/// Result of loading appearance settings, including whether the defaults were
/// returned because the stored file was malformed or unreadable.
/// </summary>
public sealed record LoadAppearanceResult
{
    public required OperatorAppearanceSettings Settings { get; init; }
    public required bool RecoveredFromMalformed { get; init; }
}

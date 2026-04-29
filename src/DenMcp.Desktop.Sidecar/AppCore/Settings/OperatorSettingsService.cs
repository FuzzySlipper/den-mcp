using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

public sealed record OperatorSettingsStorage
{
    public const string SettingsFileName = "operator-settings.json";

    public required string SettingsPath { get; init; }

    public static OperatorSettingsStorage ForPath(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        return new OperatorSettingsStorage { SettingsPath = Path.GetFullPath(settingsPath) };
    }

    public static OperatorSettingsStorage CreateDefault()
    {
        return ForPath(DefaultSettingsPath());
    }

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
}

public sealed class OperatorSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
                JsonSerializer.Serialize(stream, settings.Normalized(_sourceInstanceIdFactory), JsonOptions);
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

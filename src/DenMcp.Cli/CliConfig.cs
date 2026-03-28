using System.Text.Json;

namespace DenMcp.Cli;

public sealed class CliConfig
{
    public string ServerUrl { get; set; } = "http://localhost:5199";

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".den-mcp");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "cli-config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static CliConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new CliConfig();

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<CliConfig>(json, JsonOpts) ?? new CliConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }
}

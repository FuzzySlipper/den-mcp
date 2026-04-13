namespace DenMcp.Server;

public sealed class DenMcpOptions
{
    public string DatabasePath { get; set; } = "";
    public string ListenUrl { get; set; } = "http://localhost:5199";
    public SignalOptions Signal { get; set; } = new();

    public string GetResolvedDatabasePath()
    {
        if (!string.IsNullOrEmpty(DatabasePath))
            return DatabasePath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".den-mcp", "den.db");
    }
}

public sealed class SignalOptions
{
    public bool Enabled { get; set; }
    public string? Account { get; set; }
    public string? Recipient { get; set; }
    public string? RecipientNumber { get; set; }
    public string SignalCliPath { get; set; } = "signal-cli";
    public string HttpHost { get; set; } = "127.0.0.1";
    public int HttpPort { get; set; } = 8080;
    public bool AutoStart { get; set; }
    public bool NotifyOnDispatch { get; set; } = true;
    public bool NotifyOnAgentStatus { get; set; } = true;

    public string GetBaseUrl() => $"http://{HttpHost}:{HttpPort}";

    public string? GetConfiguredRecipient()
    {
        if (!string.IsNullOrWhiteSpace(Recipient))
            return Recipient.Trim();

        return string.IsNullOrWhiteSpace(RecipientNumber) ? null : RecipientNumber.Trim();
    }
}

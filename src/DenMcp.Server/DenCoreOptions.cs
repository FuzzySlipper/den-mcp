namespace DenMcp.Server;

public sealed class DenCoreOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5199";
    public int TimeoutSeconds { get; set; } = 5;
    public int Retries { get; set; } = 0;
    public string? ServiceToken { get; set; }

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(1, TimeoutSeconds));
}

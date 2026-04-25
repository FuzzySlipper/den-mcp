namespace DenMcp.Core.Models;

public sealed class SubagentRunSummary
{
    public required string RunId { get; set; }
    public required string State { get; set; }
    public required AgentStreamEntry Latest { get; set; }
    public AgentStreamEntry? Started { get; set; }
    public string? Role { get; set; }
    public int? TaskId { get; set; }
    public string? ProjectId { get; set; }
    public string? Backend { get; set; }
    public string? Model { get; set; }
    public string? OutputStatus { get; set; }
    public string? TimeoutKind { get; set; }
    public int? DurationMs { get; set; }
    public string? ArtifactDir { get; set; }
    public int EventCount { get; set; }
}

public sealed class SubagentRunDetail
{
    public required SubagentRunSummary Summary { get; set; }
    public required List<AgentStreamEntry> Events { get; set; }
}

public sealed class SubagentRunListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int Limit { get; set; } = 8;
    public int SourceLimit { get; set; } = 200;
}

using System.Text.Json;

namespace DenMcp.Core.Models;

public sealed class SubagentRunSummary
{
    public required string RunId { get; set; }
    public required string State { get; set; }
    public string? Schema { get; set; }
    public int? SchemaVersion { get; set; }
    public required AgentStreamEntry Latest { get; set; }
    public AgentStreamEntry? Started { get; set; }
    public string? Role { get; set; }
    public int? TaskId { get; set; }
    public string? ProjectId { get; set; }
    public string? Backend { get; set; }
    public string? Model { get; set; }
    public string? OutputStatus { get; set; }
    public string? TimeoutKind { get; set; }
    public string? InfrastructureFailureReason { get; set; }
    public string? InfrastructureWarningReason { get; set; }
    public int? ExitCode { get; set; }
    public string? Signal { get; set; }
    public int? Pid { get; set; }
    public string? StderrPreview { get; set; }
    public string? FallbackModel { get; set; }
    public string? FallbackFromModel { get; set; }
    public int? FallbackFromExitCode { get; set; }
    public int HeartbeatCount { get; set; }
    public int AssistantOutputCount { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime? LastAssistantOutputAt { get; set; }
    public int? DurationMs { get; set; }
    public string? ArtifactDir { get; set; }
    public int EventCount { get; set; }
}

public sealed class SubagentRunDetail
{
    public required SubagentRunSummary Summary { get; set; }
    public required List<AgentStreamEntry> Events { get; set; }
    public List<JsonElement> WorkEvents { get; set; } = [];
    public SubagentRunArtifactSnapshot? Artifacts { get; set; }
}

public sealed class SubagentRunArtifactSnapshot
{
    public required string Dir { get; set; }
    public bool Readable { get; set; }
    public string? StatusJson { get; set; }
    public string? EventsTail { get; set; }
    public string? StdoutTail { get; set; }
    public string? StderrTail { get; set; }
    public string? SessionFilePath { get; set; }
    public string? SessionTail { get; set; }
    public string? ReadError { get; set; }
}

public sealed class SubagentRunListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? State { get; set; }
    public int Limit { get; set; } = 8;
    public int SourceLimit { get; set; } = 200;
}

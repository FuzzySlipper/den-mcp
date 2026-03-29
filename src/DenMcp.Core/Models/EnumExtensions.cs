namespace DenMcp.Core.Models;

public static class EnumExtensions
{
    public static string ToDbValue(this TaskStatus status) => status switch
    {
        TaskStatus.Planned => "planned",
        TaskStatus.InProgress => "in_progress",
        TaskStatus.Review => "review",
        TaskStatus.Blocked => "blocked",
        TaskStatus.Done => "done",
        TaskStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static TaskStatus ParseTaskStatus(string value) => value switch
    {
        "planned" => TaskStatus.Planned,
        "in_progress" => TaskStatus.InProgress,
        "review" => TaskStatus.Review,
        "blocked" => TaskStatus.Blocked,
        "done" => TaskStatus.Done,
        "cancelled" => TaskStatus.Cancelled,
        _ => throw new ArgumentException($"Unknown task status: {value}", nameof(value))
    };

    public static string ToDbValue(this DocType docType) => docType switch
    {
        DocType.Prd => "prd",
        DocType.Spec => "spec",
        DocType.Adr => "adr",
        DocType.Convention => "convention",
        DocType.Reference => "reference",
        DocType.Note => "note",
        _ => throw new ArgumentOutOfRangeException(nameof(docType), docType, null)
    };

    public static DocType ParseDocType(string value) => value switch
    {
        "prd" => DocType.Prd,
        "spec" => DocType.Spec,
        "adr" => DocType.Adr,
        "convention" => DocType.Convention,
        "reference" => DocType.Reference,
        "note" => DocType.Note,
        _ => throw new ArgumentException($"Unknown doc type: {value}", nameof(value))
    };

    public static string ToDbValue(this AgentSessionStatus status) => status switch
    {
        AgentSessionStatus.Active => "active",
        AgentSessionStatus.Inactive => "inactive",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static AgentSessionStatus ParseAgentSessionStatus(string value) => value switch
    {
        "active" => AgentSessionStatus.Active,
        "inactive" => AgentSessionStatus.Inactive,
        _ => throw new ArgumentException($"Unknown agent session status: {value}", nameof(value))
    };
}

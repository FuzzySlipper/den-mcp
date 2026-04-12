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

    public static string ToDbValue(this DispatchStatus status) => status switch
    {
        DispatchStatus.Pending => "pending",
        DispatchStatus.Approved => "approved",
        DispatchStatus.Rejected => "rejected",
        DispatchStatus.Completed => "completed",
        DispatchStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static DispatchStatus ParseDispatchStatus(string value) => value switch
    {
        "pending" => DispatchStatus.Pending,
        "approved" => DispatchStatus.Approved,
        "rejected" => DispatchStatus.Rejected,
        "completed" => DispatchStatus.Completed,
        "expired" => DispatchStatus.Expired,
        _ => throw new ArgumentException($"Unknown dispatch status: {value}", nameof(value))
    };

    public static string ToDbValue(this DispatchTriggerType type) => type switch
    {
        DispatchTriggerType.Message => "message",
        DispatchTriggerType.TaskStatus => "task_status",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static DispatchTriggerType ParseDispatchTriggerType(string value) => value switch
    {
        "message" => DispatchTriggerType.Message,
        "task_status" => DispatchTriggerType.TaskStatus,
        _ => throw new ArgumentException($"Unknown dispatch trigger type: {value}", nameof(value))
    };

    public static string ToDbValue(this ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.ChangesRequested => "changes_requested",
        ReviewVerdict.LooksGood => "looks_good",
        ReviewVerdict.FollowUpNeeded => "follow_up_needed",
        ReviewVerdict.BlockedByDependency => "blocked_by_dependency",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null)
    };

    public static ReviewVerdict ParseReviewVerdict(string value) => value switch
    {
        "changes_requested" => ReviewVerdict.ChangesRequested,
        "looks_good" => ReviewVerdict.LooksGood,
        "follow_up_needed" => ReviewVerdict.FollowUpNeeded,
        "blocked_by_dependency" => ReviewVerdict.BlockedByDependency,
        _ => throw new ArgumentException($"Unknown review verdict: {value}", nameof(value))
    };
}

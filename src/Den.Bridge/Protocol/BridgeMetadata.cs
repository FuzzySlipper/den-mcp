using System.Text.Json;
using System.Text.Json.Serialization;

namespace Den.Bridge.Protocol;

public sealed record BridgeCorrelation
{
    public static BridgeCorrelation Empty { get; } = new();

    [JsonPropertyName("trace_id")]
    public string? TraceId { get; init; }

    [JsonPropertyName("causation_id")]
    public string? CausationId { get; init; }

    [JsonPropertyName("parent_request_id")]
    public string? ParentRequestId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("operator_session_id")]
    public string? OperatorSessionId { get; init; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }
}

public sealed record BridgeError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("details")]
    public JsonElement? Details { get; init; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }

    [JsonPropertyName("caused_by")]
    public IReadOnlyList<BridgeError>? CausedBy { get; init; }
}

public sealed record BridgeCommandCapability
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("request_schema")]
    public string? RequestSchema { get; init; }

    [JsonPropertyName("response_schema")]
    public string? ResponseSchema { get; init; }

    [JsonPropertyName("supports_cancellation")]
    public bool SupportsCancellation { get; init; }

    [JsonPropertyName("supports_progress")]
    public bool SupportsProgress { get; init; }

    [JsonPropertyName("required_capabilities")]
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();
}

public sealed record BridgeEventCapability
{
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("payload_schema")]
    public string? PayloadSchema { get; init; }

    [JsonPropertyName("required_capabilities")]
    public IReadOnlyList<string>? RequiredCapabilities { get; init; }
}

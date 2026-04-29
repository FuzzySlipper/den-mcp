using System.Text.Json;
using System.Text.Json.Serialization;

namespace Den.Bridge.Protocol;

public interface IBridgeFrame
{
    string ProtocolVersion { get; }

    string SchemaVersion { get; }

    string FrameType { get; }

    BridgeCorrelation Correlation { get; }

    DateTimeOffset SentAt { get; }
}

public abstract record BridgeFrame : IBridgeFrame
{
    [JsonPropertyName("protocol_version")]
    [JsonPropertyOrder(-100)]
    public string ProtocolVersion { get; init; } = BridgeProtocol.ProtocolVersion;

    [JsonPropertyName("schema_version")]
    [JsonPropertyOrder(-99)]
    public string SchemaVersion { get; init; } = BridgeProtocol.DefaultSchemaVersion;

    [JsonPropertyName("frame_type")]
    [JsonPropertyOrder(-98)]
    public abstract string FrameType { get; }

    [JsonPropertyName("correlation")]
    [JsonPropertyOrder(90)]
    public BridgeCorrelation Correlation { get; init; } = BridgeCorrelation.Empty;

    [JsonPropertyName("sent_at")]
    [JsonPropertyOrder(100)]
    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record BridgeRequestFrame : BridgeFrame
{
    public override string FrameType => BridgeFrameTypes.Request;

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; } = BridgeJson.EmptyObject();

    [JsonPropertyName("deadline_ms")]
    public int? DeadlineMs { get; init; }

    [JsonPropertyName("expects_progress")]
    public bool ExpectsProgress { get; init; }
}

public sealed record BridgeResponseFrame : BridgeFrame
{
    public override string FrameType => BridgeFrameTypes.Response;

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public BridgeError? Error { get; init; }
}

public sealed record BridgeEventFrame : BridgeFrame
{
    public override string FrameType => BridgeFrameTypes.Event;

    [JsonPropertyName("event_id")]
    public required string EventId { get; init; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; } = BridgeJson.EmptyObject();
}

public sealed record BridgeProgressFrame : BridgeFrame
{
    public override string FrameType => BridgeFrameTypes.Progress;

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("percent")]
    public double? Percent { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; } = BridgeJson.EmptyObject();
}

public sealed record BridgeCancelFrame : BridgeFrame
{
    public override string FrameType => BridgeFrameTypes.Cancel;

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record BridgeHealthFrame : BridgeFrame
{
    public override string FrameType => BridgeFrameTypes.Health;

    [JsonPropertyName("process_id")]
    public int ProcessId { get; init; }

    [JsonPropertyName("uptime_ms")]
    public long UptimeMs { get; init; }

    [JsonPropertyName("ready_state")]
    public required string ReadyState { get; init; }

    [JsonPropertyName("app_id")]
    public required string AppId { get; init; }

    [JsonPropertyName("app_version")]
    public required string AppVersion { get; init; }

    [JsonPropertyName("active_request_count")]
    public int ActiveRequestCount { get; init; }

    [JsonPropertyName("degraded_subsystems")]
    public IReadOnlyList<string> DegradedSubsystems { get; init; } = Array.Empty<string>();

    [JsonPropertyName("last_error")]
    public BridgeError? LastError { get; init; }
}

public sealed record BridgeCapabilitiesFrame : BridgeFrame
{
    public override string FrameType => BridgeFrameTypes.Capabilities;

    [JsonPropertyName("app_id")]
    public required string AppId { get; init; }

    [JsonPropertyName("app_version")]
    public required string AppVersion { get; init; }

    [JsonPropertyName("supported_transports")]
    public IReadOnlyList<string> SupportedTransports { get; init; } = Array.Empty<string>();

    [JsonPropertyName("commands")]
    public IReadOnlyList<BridgeCommandCapability> Commands { get; init; } = Array.Empty<BridgeCommandCapability>();

    [JsonPropertyName("events")]
    public IReadOnlyList<BridgeEventCapability> Events { get; init; } = Array.Empty<BridgeEventCapability>();

    [JsonPropertyName("feature_flags")]
    public IReadOnlyList<string> FeatureFlags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("schema_bundle_id")]
    public required string SchemaBundleId { get; init; }
}

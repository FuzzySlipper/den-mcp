using System.Text.Json;
using System.Text.Json.Serialization;
using Den.Bridge.Protocol;
using Den.Bridge.Registry;

namespace Den.Bridge.Schema;

public sealed record BridgeNamedSchema(string Name, JsonElement Schema);

public sealed record BridgeSchemaBundle
{
    public const string CurrentBundleKind = "den.bridge.schema_bundle";

    [JsonPropertyName("bundle_kind")]
    [JsonPropertyOrder(-100)]
    public string BundleKind { get; init; } = CurrentBundleKind;

    /// <summary>
    /// Version of the schema-bundle container contract. Version 1 describes the
    /// bundle artifact shape (metadata, definitions, command/event indexes), not
    /// the app schema content revision. Increment only for breaking changes to
    /// this bundle JSON shape; content/tool compatibility is expressed through
    /// <see cref="BundleId" />, <see cref="ProtocolVersion" />, and
    /// <see cref="SchemaVersion" />.
    /// </summary>
    [JsonPropertyName("version")]
    [JsonPropertyOrder(-99)]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Stable identity for the exact schema bundle content emitted by tooling.
    /// </summary>
    [JsonPropertyName("bundle_id")]
    [JsonPropertyOrder(-98)]
    public required string BundleId { get; init; }

    [JsonPropertyName("protocol_version")]
    [JsonPropertyOrder(-97)]
    public string ProtocolVersion { get; init; } = BridgeProtocol.ProtocolVersion;

    /// <summary>
    /// App schema/content compatibility version compared during bridge startup.
    /// </summary>
    [JsonPropertyName("schema_version")]
    [JsonPropertyOrder(-96)]
    public string SchemaVersion { get; init; } = BridgeProtocol.DefaultSchemaVersion;

    [JsonPropertyName("definitions")]
    [JsonPropertyOrder(-10)]
    public IReadOnlyDictionary<string, JsonElement> Definitions { get; init; } = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);

    [JsonPropertyName("commands")]
    [JsonPropertyOrder(10)]
    public IReadOnlyList<BridgeCommandSchema> Commands { get; init; } = Array.Empty<BridgeCommandSchema>();

    [JsonPropertyName("events")]
    [JsonPropertyOrder(20)]
    public IReadOnlyList<BridgeEventSchema> Events { get; init; } = Array.Empty<BridgeEventSchema>();
}

public sealed record BridgeCommandSchema
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("request_schema")]
    public required string RequestSchema { get; init; }

    [JsonPropertyName("response_schema")]
    public required string ResponseSchema { get; init; }

    [JsonPropertyName("supports_cancellation")]
    public bool SupportsCancellation { get; init; }

    [JsonPropertyName("supports_progress")]
    public bool SupportsProgress { get; init; }

    [JsonPropertyName("required_capabilities")]
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();
}

public sealed record BridgeEventSchema
{
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("payload_schema")]
    public required string PayloadSchema { get; init; }

    [JsonPropertyName("required_capabilities")]
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();
}

public static class BridgeSchemaBundleFactory
{
    public static BridgeSchemaBundle Create(
        string bundleId,
        string? schemaVersion = null,
        IBridgeCommandRegistry? commandRegistry = null,
        IBridgeEventRegistry? eventRegistry = null,
        IEnumerable<BridgeNamedSchema>? payloadSchemas = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);

        var definitions = CreateProtocolDefinitions();
        if (payloadSchemas is not null)
        {
            foreach (var schema in payloadSchemas.OrderBy(schema => schema.Name, StringComparer.Ordinal))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(schema.Name);
                if (!definitions.TryAdd(schema.Name, schema.Schema.Clone()))
                {
                    throw new InvalidOperationException($"Bridge schema '{schema.Name}' is already defined.");
                }
            }
        }

        return new BridgeSchemaBundle
        {
            BundleId = bundleId,
            SchemaVersion = schemaVersion ?? BridgeProtocol.DefaultSchemaVersion,
            Definitions = definitions,
            Commands = (commandRegistry?.Commands ?? Array.Empty<BridgeCommandDescriptor>())
                .OrderBy(command => command.Command, StringComparer.Ordinal)
                .Select(command => new BridgeCommandSchema
                {
                    Command = command.Command,
                    RequestSchema = command.RequestSchema ?? command.Command + ".request",
                    ResponseSchema = command.ResponseSchema ?? command.Command + ".response",
                    SupportsCancellation = command.SupportsCancellation,
                    SupportsProgress = command.SupportsProgress,
                    RequiredCapabilities = command.RequiredCapabilities,
                })
                .ToArray(),
            Events = (eventRegistry?.Events ?? Array.Empty<BridgeEventDescriptor>())
                .OrderBy(@event => @event.Event, StringComparer.Ordinal)
                .Select(@event => new BridgeEventSchema
                {
                    Event = @event.Event,
                    PayloadSchema = @event.PayloadSchema ?? @event.Event + ".payload",
                    RequiredCapabilities = @event.RequiredCapabilities,
                })
                .ToArray(),
        };
    }

    private static SortedDictionary<string, JsonElement> CreateProtocolDefinitions()
    {
        return new SortedDictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["bridge.cancel_frame"] = Schema("""
                {"type":"object","additionalProperties":false,"required":["protocol_version","schema_version","frame_type","request_id"],"properties":{"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"frame_type":{"const":"cancel"},"request_id":{"type":"string"},"reason":{"type":"string"},"correlation":{"$ref":"bridge.correlation"},"sent_at":{"type":"string","format":"date-time"}}}
                """),
            ["bridge.capabilities_frame"] = Schema("""
                {"type":"object","additionalProperties":false,"required":["protocol_version","schema_version","frame_type","app_id","app_version","supported_transports","commands","events","feature_flags","schema_bundle_id"],"properties":{"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"frame_type":{"const":"capabilities"},"app_id":{"type":"string"},"app_version":{"type":"string"},"supported_transports":{"type":"array","items":{"type":"string"}},"commands":{"type":"array","items":{"$ref":"bridge.command_capability"}},"events":{"type":"array","items":{"$ref":"bridge.event_capability"}},"feature_flags":{"type":"array","items":{"type":"string"}},"schema_bundle_id":{"type":"string"},"correlation":{"$ref":"bridge.correlation"},"sent_at":{"type":"string","format":"date-time"}}}
                """),
            ["bridge.command_capability"] = Schema("""
                {"type":"object","additionalProperties":false,"required":["command","supports_cancellation","supports_progress"],"properties":{"command":{"type":"string"},"request_schema":{"type":"string"},"response_schema":{"type":"string"},"supports_cancellation":{"type":"boolean"},"supports_progress":{"type":"boolean"},"required_capabilities":{"type":"array","items":{"type":"string"}}}}
                """),
            ["bridge.correlation"] = Schema("""
                {"type":"object","additionalProperties":false,"properties":{"trace_id":{"type":"string"},"causation_id":{"type":"string"},"parent_request_id":{"type":"string"},"task_id":{"type":"integer"},"operator_session_id":{"type":"string"},"metadata":{"type":"object","additionalProperties":true}}}
                """),
            ["bridge.error"] = Schema("""
                {"type":"object","additionalProperties":false,"required":["code","message","category"],"properties":{"code":{"type":"string"},"message":{"type":"string"},"category":{"enum":["validation","not_found","conflict","unauthorized","transient","cancelled","internal","unavailable","unsupported_capability"]},"details":{"$ref":"bridge.json_value"},"retryable":{"type":"boolean"},"caused_by":{"type":"array","items":{"$ref":"bridge.error"}}}}
                """),
            ["bridge.event_capability"] = Schema("""
                {"type":"object","additionalProperties":false,"required":["event"],"properties":{"event":{"type":"string"},"payload_schema":{"type":"string"},"required_capabilities":{"type":"array","items":{"type":"string"}}}}
                """),
            ["bridge.event_frame"] = Schema("""
                {"type":"object","additionalProperties":false,"required":["protocol_version","schema_version","frame_type","event_id","sequence","event","payload"],"properties":{"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"frame_type":{"const":"event"},"event_id":{"type":"string"},"sequence":{"type":"integer"},"event":{"type":"string"},"payload":{"$ref":"bridge.json_value"},"correlation":{"$ref":"bridge.correlation"},"sent_at":{"type":"string","format":"date-time"}}}
                """),
            ["bridge.health_frame"] = Schema("""
                {"type":"object","additionalProperties":false,"required":["protocol_version","schema_version","frame_type","process_id","uptime_ms","ready_state","app_id","app_version","active_request_count","degraded_subsystems"],"properties":{"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"frame_type":{"const":"health"},"process_id":{"type":"integer"},"uptime_ms":{"type":"integer"},"ready_state":{"type":"string"},"app_id":{"type":"string"},"app_version":{"type":"string"},"active_request_count":{"type":"integer"},"degraded_subsystems":{"type":"array","items":{"type":"string"}},"last_error":{"$ref":"bridge.error"},"correlation":{"$ref":"bridge.correlation"},"sent_at":{"type":"string","format":"date-time"}}}
                """),
            ["bridge.json_value"] = Schema("""
                {"type":["object","array","string","number","integer","boolean","null"]}
                """),
            ["bridge.progress_frame"] = Schema("""
                {"type":"object","additionalProperties":false,"required":["protocol_version","schema_version","frame_type","request_id","stage","payload"],"properties":{"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"frame_type":{"const":"progress"},"request_id":{"type":"string"},"stage":{"type":"string"},"message":{"type":"string"},"percent":{"type":"number"},"payload":{"$ref":"bridge.json_value"},"correlation":{"$ref":"bridge.correlation"},"sent_at":{"type":"string","format":"date-time"}}}
                """),
            ["bridge.request_frame"] = Schema("""
                {"type":"object","additionalProperties":false,"required":["protocol_version","schema_version","frame_type","request_id","command","payload"],"properties":{"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"frame_type":{"const":"request"},"request_id":{"type":"string"},"command":{"type":"string"},"payload":{"$ref":"bridge.json_value"},"deadline_ms":{"type":"integer"},"expects_progress":{"type":"boolean"},"correlation":{"$ref":"bridge.correlation"},"sent_at":{"type":"string","format":"date-time"}}}
                """),
            ["bridge.response_frame"] = Schema("""
                {"type":"object","additionalProperties":false,"required":["protocol_version","schema_version","frame_type","request_id"],"oneOf":[{"required":["result"]},{"required":["error"]}],"properties":{"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"frame_type":{"const":"response"},"request_id":{"type":"string"},"result":{"$ref":"bridge.json_value"},"error":{"$ref":"bridge.error"},"correlation":{"$ref":"bridge.correlation"},"sent_at":{"type":"string","format":"date-time"}}}
                """),
        };
    }

    public static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

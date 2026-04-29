using System.Text.Json.Serialization;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using Den.Bridge.Registry;
using Den.Bridge.Schema;

namespace Den.Bridge.Tests;

public class BridgeSchemaCompatibilityTests
{
    private static readonly DateTimeOffset TestTimestamp = new(2026, 4, 29, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void SchemaBundle_SerializesDeterministicallyForProtocolAndRepresentativeDtos()
    {
        var bundle = CreateSampleBundle();
        var actualJson = BridgeJson.Serialize(bundle);
        var expectedJson = ReadFixture("sample-schema-bundle.json");

        Assert.Equal(expectedJson, actualJson);
        Assert.Contains("bridge.request_frame", bundle.Definitions.Keys);
        Assert.Contains("sample.echo.request", bundle.Definitions.Keys);
        Assert.Contains("sample.echo.response", bundle.Definitions.Keys);
        Assert.Contains("sample.echoed.payload", bundle.Definitions.Keys);
    }

    [Fact]
    public void WireFrameFixture_SerializesCSharpRepresentativeFramesForTypeScriptContractTests()
    {
        var fixture = CreateWireFrameFixture();
        var actualJson = BridgeJson.Serialize(fixture);
        var expectedJson = ReadFixture("sample-wire-frames.json");

        Assert.Equal(expectedJson, actualJson);
        Assert.Equal("request", fixture.Frames.Request.FrameType);
        Assert.Equal("response", fixture.Frames.ResponseSuccess.FrameType);
        Assert.Equal("event", fixture.Frames.Event.FrameType);
        Assert.Equal("progress", fixture.Frames.Progress.FrameType);
        Assert.Equal("cancel", fixture.Frames.Cancel.FrameType);
        Assert.Equal("health", fixture.Frames.Health.FrameType);
        Assert.Equal("capabilities", fixture.Frames.Capabilities.FrameType);
        Assert.Equal(BridgeErrorCategories.Transient, fixture.Frames.ResponseError.Error?.Category);
    }

    internal static BridgeSchemaBundle CreateSampleBundle()
    {
        var builder = new BridgeRegistryBuilder()
            .RegisterCommand<SampleEchoRequest, SampleEchoResponse, SampleEchoHandler>(
                "sample.echo",
                registration =>
                {
                    registration.SupportsCancellation = true;
                    registration.SupportsProgress = true;
                    registration.RequiredCapabilities = new[] { "sample.echo" };
                })
            .RegisterEvent<SampleEchoedPayload>(
                "sample.echoed",
                registration => registration.RequiredCapabilities = new[] { "sample.read" });

        return BridgeSchemaBundleFactory.Create(
            "sample.bridge@2026-04-29",
            "den-desktop@2026-04-29",
            builder.BuildCommandRegistry(),
            builder.BuildEventRegistry(),
            CreateSamplePayloadSchemas());
    }

    internal static BridgeContractWireFrameFixture CreateWireFrameFixture()
    {
        var correlation = new BridgeCorrelation { TraceId = "tr_001", TaskId = 979 };

        return new BridgeContractWireFrameFixture
        {
            SchemaBundleId = "sample.bridge@2026-04-29",
            Frames = new BridgeContractFrames
            {
                Request = new BridgeRequestFrame
                {
                    SchemaVersion = "den-desktop@2026-04-29",
                    RequestId = "req_001",
                    Command = "sample.echo",
                    Payload = BridgeJson.ToElement(new SampleEchoRequest { Message = "hello" }),
                    Correlation = correlation,
                    SentAt = TestTimestamp,
                    DeadlineMs = 30000,
                    ExpectsProgress = true,
                },
                ResponseSuccess = BridgeResponseFrame.Success(
                    "req_001",
                    BridgeJson.ToElement(new SampleEchoResponse { Echo = "hello", RequestId = "req_001" }),
                    correlation,
                    TestTimestamp,
                    "den-desktop@2026-04-29"),
                ResponseError = BridgeResponseFrame.Failure(
                    "req_002",
                    new BridgeError
                    {
                        Code = "sample.failed",
                        Message = "Sample failure",
                        Category = BridgeErrorCategories.Transient,
                        Details = BridgeJson.ToElement(new { RetryAfterMs = 250 }),
                        Retryable = true,
                    },
                    new BridgeCorrelation { TraceId = "tr_002" },
                    TestTimestamp,
                    "den-desktop@2026-04-29"),
                Event = new BridgeEventFrame
                {
                    SchemaVersion = "den-desktop@2026-04-29",
                    EventId = "evt_001",
                    Sequence = 1,
                    Event = "sample.echoed",
                    Payload = BridgeJson.ToElement(new SampleEchoedPayload { Message = "hello", RequestId = "req_001" }),
                    Correlation = correlation,
                    SentAt = TestTimestamp,
                },
                Progress = new BridgeProgressFrame
                {
                    SchemaVersion = "den-desktop@2026-04-29",
                    RequestId = "req_001",
                    Stage = "echoing",
                    Message = "Echoing payload",
                    Percent = 50,
                    Payload = BridgeJson.ToElement(new { Step = "handler" }),
                    Correlation = correlation,
                    SentAt = TestTimestamp,
                },
                Cancel = new BridgeCancelFrame
                {
                    SchemaVersion = "den-desktop@2026-04-29",
                    RequestId = "req_001",
                    Reason = "user_requested",
                    Correlation = correlation,
                    SentAt = TestTimestamp,
                },
                Health = new BridgeHealthFrame
                {
                    SchemaVersion = "den-desktop@2026-04-29",
                    ProcessId = 1234,
                    UptimeMs = 5000,
                    ReadyState = "ready",
                    AppId = "sample-app",
                    AppVersion = "0.1.0",
                    ActiveRequestCount = 1,
                    DegradedSubsystems = Array.Empty<string>(),
                    LastError = null,
                    Correlation = BridgeCorrelation.Empty,
                    SentAt = TestTimestamp,
                },
                Capabilities = new BridgeCapabilitiesFrame
                {
                    SchemaVersion = "den-desktop@2026-04-29",
                    AppId = "sample-app",
                    AppVersion = "0.1.0",
                    SupportedTransports = new[] { "in_memory" },
                    Commands = new[]
                    {
                        new BridgeCommandCapability
                        {
                            Command = "sample.echo",
                            RequestSchema = "sample.echo.request",
                            ResponseSchema = "sample.echo.response",
                            SupportsCancellation = true,
                            SupportsProgress = true,
                            RequiredCapabilities = new[] { "sample.echo" },
                        },
                    },
                    Events = new[]
                    {
                        new BridgeEventCapability
                        {
                            Event = "sample.echoed",
                            PayloadSchema = "sample.echoed.payload",
                            RequiredCapabilities = new[] { "sample.read" },
                        },
                    },
                    FeatureFlags = new[] { "bridge.contract_test" },
                    SchemaBundleId = "sample.bridge@2026-04-29",
                    Correlation = BridgeCorrelation.Empty,
                    SentAt = TestTimestamp,
                },
            },
        };
    }

    private static IReadOnlyList<BridgeNamedSchema> CreateSamplePayloadSchemas()
    {
        return new[]
        {
            new BridgeNamedSchema(
                "sample.echo.request",
                BridgeSchemaBundleFactory.Schema("""
                    {"type":"object","additionalProperties":false,"required":["message"],"properties":{"message":{"type":"string"}}}
                    """)),
            new BridgeNamedSchema(
                "sample.echo.response",
                BridgeSchemaBundleFactory.Schema("""
                    {"type":"object","additionalProperties":false,"required":["echo","request_id"],"properties":{"echo":{"type":"string"},"request_id":{"type":"string"}}}
                    """)),
            new BridgeNamedSchema(
                "sample.echoed.payload",
                BridgeSchemaBundleFactory.Schema("""
                    {"type":"object","additionalProperties":false,"required":["message","request_id"],"properties":{"message":{"type":"string"},"request_id":{"type":"string"}}}
                    """)),
        };
    }

    private static string ReadFixture(string fixtureName)
    {
        return File.ReadAllText(Path.Combine(RepositoryRoot(), "testdata", "bridge-contract", fixtureName)).Trim();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "den-mcp.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    internal sealed record BridgeContractWireFrameFixture
    {
        [JsonPropertyName("schema_bundle_id")]
        public required string SchemaBundleId { get; init; }

        [JsonPropertyName("frames")]
        public required BridgeContractFrames Frames { get; init; }
    }

    internal sealed record BridgeContractFrames
    {
        [JsonPropertyName("request")]
        public required BridgeRequestFrame Request { get; init; }

        [JsonPropertyName("response_success")]
        public required BridgeResponseFrame ResponseSuccess { get; init; }

        [JsonPropertyName("response_error")]
        public required BridgeResponseFrame ResponseError { get; init; }

        [JsonPropertyName("event")]
        public required BridgeEventFrame Event { get; init; }

        [JsonPropertyName("progress")]
        public required BridgeProgressFrame Progress { get; init; }

        [JsonPropertyName("cancel")]
        public required BridgeCancelFrame Cancel { get; init; }

        [JsonPropertyName("health")]
        public required BridgeHealthFrame Health { get; init; }

        [JsonPropertyName("capabilities")]
        public required BridgeCapabilitiesFrame Capabilities { get; init; }
    }

    private sealed record SampleEchoRequest
    {
        public required string Message { get; init; }
    }

    private sealed record SampleEchoResponse
    {
        public required string Echo { get; init; }

        public required string RequestId { get; init; }
    }

    private sealed record SampleEchoedPayload
    {
        public required string Message { get; init; }

        public required string RequestId { get; init; }
    }

    private sealed class SampleEchoHandler : IBridgeCommandHandler<SampleEchoRequest, SampleEchoResponse>
    {
        public ValueTask<SampleEchoResponse?> HandleAsync(
            SampleEchoRequest request,
            BridgeRequestContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<SampleEchoResponse?>(new SampleEchoResponse
            {
                Echo = request.Message,
                RequestId = context.RequestId,
            });
        }
    }
}

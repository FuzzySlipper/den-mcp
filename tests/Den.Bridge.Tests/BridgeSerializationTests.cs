using Den.Bridge.Protocol;

namespace Den.Bridge.Tests;

public class BridgeSerializationTests
{
    private static readonly DateTimeOffset TestTimestamp = new(2026, 4, 29, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void RequestFrame_SerializesStableSnakeCaseEnvelope()
    {
        var frame = new BridgeRequestFrame
        {
            SchemaVersion = "den-desktop@2026-04-29",
            RequestId = "req_001",
            Command = "sample.echo",
            Payload = BridgeJson.ToElement(new { Message = "hello" }),
            Correlation = new BridgeCorrelation { TraceId = "tr_001", TaskId = 978 },
            SentAt = TestTimestamp,
            DeadlineMs = 30000,
            ExpectsProgress = true,
        };

        var json = BridgeJson.Serialize(frame);

        Assert.Equal(
            "{\"protocol_version\":\"1.0\",\"schema_version\":\"den-desktop@2026-04-29\",\"frame_type\":\"request\",\"request_id\":\"req_001\",\"command\":\"sample.echo\",\"payload\":{\"message\":\"hello\"},\"deadline_ms\":30000,\"expects_progress\":true,\"correlation\":{\"trace_id\":\"tr_001\",\"task_id\":978},\"sent_at\":\"2026-04-29T12:34:56+00:00\"}",
            json);
    }

    [Fact]
    public void ResponseFrame_SerializesResultAndErrorShapes()
    {
        var success = BridgeResponseFrame.Success(
            "req_001",
            BridgeJson.ToElement(new { SnapshotVersion = 42 }),
            new BridgeCorrelation { TraceId = "tr_001" },
            TestTimestamp,
            "den-desktop@2026-04-29");

        var failure = BridgeResponseFrame.Failure(
            "req_002",
            new BridgeError
            {
                Code = "sample.failed",
                Message = "Sample failure",
                Category = BridgeErrorCategories.Transient,
                Details = BridgeJson.ToElement(new { RetryAfterMs = 250 }),
                Retryable = true,
                CausedBy = new[]
                {
                    new BridgeError
                    {
                        Code = "io.timeout",
                        Message = "Timed out",
                        Category = BridgeErrorCategories.Transient,
                        Retryable = true,
                    },
                },
            },
            new BridgeCorrelation { TraceId = "tr_002" },
            TestTimestamp,
            "den-desktop@2026-04-29");

        Assert.Equal(
            "{\"protocol_version\":\"1.0\",\"schema_version\":\"den-desktop@2026-04-29\",\"frame_type\":\"response\",\"request_id\":\"req_001\",\"result\":{\"snapshot_version\":42},\"correlation\":{\"trace_id\":\"tr_001\"},\"sent_at\":\"2026-04-29T12:34:56+00:00\"}",
            BridgeJson.Serialize(success));
        Assert.Equal(
            "{\"protocol_version\":\"1.0\",\"schema_version\":\"den-desktop@2026-04-29\",\"frame_type\":\"response\",\"request_id\":\"req_002\",\"error\":{\"code\":\"sample.failed\",\"message\":\"Sample failure\",\"category\":\"transient\",\"details\":{\"retry_after_ms\":250},\"retryable\":true,\"caused_by\":[{\"code\":\"io.timeout\",\"message\":\"Timed out\",\"category\":\"transient\",\"retryable\":true}]},\"correlation\":{\"trace_id\":\"tr_002\"},\"sent_at\":\"2026-04-29T12:34:56+00:00\"}",
            BridgeJson.Serialize(failure));
    }

    [Fact]
    public void ResponseFrame_HelpersSetExactlyOneOfResultOrError()
    {
        var success = BridgeResponseFrame.Success("req_success");
        var failure = BridgeResponseFrame.Failure(
            "req_failure",
            "sample.failed",
            "Sample failure",
            BridgeErrorCategories.Internal);

        Assert.NotNull(success.Result);
        Assert.Null(success.Error);
        Assert.Null(failure.Result);
        Assert.NotNull(failure.Error);
        Assert.Equal("sample.failed", failure.Error.Code);
        Assert.Equal(BridgeErrorCategories.Internal, failure.Error.Category);
    }

    [Fact]
    public void ResponseFrame_ConstructorRejectsMissingOrAmbiguousResultError()
    {
        var error = new BridgeError
        {
            Code = "sample.failed",
            Message = "Sample failure",
            Category = BridgeErrorCategories.Internal,
        };

        Assert.Throws<ArgumentException>(() => new BridgeResponseFrame("req_missing"));
        Assert.Throws<ArgumentException>(() => new BridgeResponseFrame("req_ambiguous", BridgeJson.EmptyObject(), error));
    }

    [Fact]
    public void EventProgressCancelHealthAndCapabilitiesFrames_SerializeStableSnakeCase()
    {
        var evt = new BridgeEventFrame
        {
            SchemaVersion = "den-desktop@2026-04-29",
            EventId = "evt_001",
            Sequence = 128,
            Event = "sample.changed",
            Payload = BridgeJson.ToElement(new { Changed = true }),
            Correlation = new BridgeCorrelation { TraceId = "tr_003" },
            SentAt = TestTimestamp,
        };
        var progress = new BridgeProgressFrame
        {
            SchemaVersion = "den-desktop@2026-04-29",
            RequestId = "req_001",
            Stage = "fetching",
            Message = "Fetching sample data",
            Percent = 35,
            Payload = BridgeJson.ToElement(new { Step = "download" }),
            Correlation = new BridgeCorrelation { TraceId = "tr_001" },
            SentAt = TestTimestamp,
        };
        var cancel = new BridgeCancelFrame
        {
            SchemaVersion = "den-desktop@2026-04-29",
            RequestId = "req_001",
            Reason = "user_requested",
            Correlation = new BridgeCorrelation { TraceId = "tr_001" },
            SentAt = TestTimestamp,
        };
        var health = new BridgeHealthFrame
        {
            SchemaVersion = "den-desktop@2026-04-29",
            ProcessId = 1234,
            UptimeMs = 5000,
            ReadyState = "ready",
            AppId = "sample-app",
            AppVersion = "0.1.0",
            ActiveRequestCount = 2,
            DegradedSubsystems = new[] { "git" },
            Correlation = BridgeCorrelation.Empty,
            SentAt = TestTimestamp,
        };
        var capabilities = new BridgeCapabilitiesFrame
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
                    RequiredCapabilities = new[] { "sample" },
                },
            },
            Events = new[] { new BridgeEventCapability { Event = "sample.changed", PayloadSchema = "sample.changed" } },
            FeatureFlags = new[] { "bridge.test" },
            SchemaBundleId = "sample@2026-04-29",
            Correlation = BridgeCorrelation.Empty,
            SentAt = TestTimestamp,
        };

        Assert.Equal(
            "{\"protocol_version\":\"1.0\",\"schema_version\":\"den-desktop@2026-04-29\",\"frame_type\":\"event\",\"event_id\":\"evt_001\",\"sequence\":128,\"event\":\"sample.changed\",\"payload\":{\"changed\":true},\"correlation\":{\"trace_id\":\"tr_003\"},\"sent_at\":\"2026-04-29T12:34:56+00:00\"}",
            BridgeJson.Serialize(evt));
        Assert.Equal(
            "{\"protocol_version\":\"1.0\",\"schema_version\":\"den-desktop@2026-04-29\",\"frame_type\":\"progress\",\"request_id\":\"req_001\",\"stage\":\"fetching\",\"message\":\"Fetching sample data\",\"percent\":35,\"payload\":{\"step\":\"download\"},\"correlation\":{\"trace_id\":\"tr_001\"},\"sent_at\":\"2026-04-29T12:34:56+00:00\"}",
            BridgeJson.Serialize(progress));
        Assert.Equal(
            "{\"protocol_version\":\"1.0\",\"schema_version\":\"den-desktop@2026-04-29\",\"frame_type\":\"cancel\",\"request_id\":\"req_001\",\"reason\":\"user_requested\",\"correlation\":{\"trace_id\":\"tr_001\"},\"sent_at\":\"2026-04-29T12:34:56+00:00\"}",
            BridgeJson.Serialize(cancel));
        Assert.Equal(
            "{\"protocol_version\":\"1.0\",\"schema_version\":\"den-desktop@2026-04-29\",\"frame_type\":\"health\",\"process_id\":1234,\"uptime_ms\":5000,\"ready_state\":\"ready\",\"app_id\":\"sample-app\",\"app_version\":\"0.1.0\",\"active_request_count\":2,\"degraded_subsystems\":[\"git\"],\"correlation\":{},\"sent_at\":\"2026-04-29T12:34:56+00:00\"}",
            BridgeJson.Serialize(health));
        Assert.Equal(
            "{\"protocol_version\":\"1.0\",\"schema_version\":\"den-desktop@2026-04-29\",\"frame_type\":\"capabilities\",\"app_id\":\"sample-app\",\"app_version\":\"0.1.0\",\"supported_transports\":[\"in_memory\"],\"commands\":[{\"command\":\"sample.echo\",\"request_schema\":\"sample.echo.request\",\"response_schema\":\"sample.echo.response\",\"supports_cancellation\":true,\"supports_progress\":true,\"required_capabilities\":[\"sample\"]}],\"events\":[{\"event\":\"sample.changed\",\"payload_schema\":\"sample.changed\"}],\"feature_flags\":[\"bridge.test\"],\"schema_bundle_id\":\"sample@2026-04-29\",\"correlation\":{},\"sent_at\":\"2026-04-29T12:34:56+00:00\"}",
            BridgeJson.Serialize(capabilities));
    }
}

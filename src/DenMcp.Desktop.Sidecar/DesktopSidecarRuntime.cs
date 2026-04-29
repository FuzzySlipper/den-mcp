using Den.Bridge.Protocol;

namespace DenMcp.Desktop.Sidecar;

public sealed class DesktopSidecarRuntimeState
{
    private long _sequence;

    public DesktopSidecarRuntimeState(DesktopSidecarOptions options, DateTimeOffset? startedAt = null)
    {
        Options = options;
        StartedAt = startedAt ?? DateTimeOffset.UtcNow;
    }

    public DesktopSidecarOptions Options { get; }

    public DateTimeOffset StartedAt { get; }

    public string ReadyState { get; set; } = "ready";

    public int ActiveRequestCount { get; set; }

    public IReadOnlyList<string> DegradedSubsystems { get; set; } = Array.Empty<string>();

    public BridgeError? LastError { get; set; }

    public long UptimeMs(DateTimeOffset? now = null)
    {
        return Math.Max(0, (long)((now ?? DateTimeOffset.UtcNow) - StartedAt).TotalMilliseconds);
    }

    public long NextSequence()
    {
        return Interlocked.Increment(ref _sequence);
    }

    public DesktopSidecarHealthResponse CreateHealth(DateTimeOffset? now = null)
    {
        return new DesktopSidecarHealthResponse
        {
            ProcessId = Environment.ProcessId,
            UptimeMs = UptimeMs(now),
            ReadyState = ReadyState,
            AppId = Options.AppId,
            AppVersion = Options.AppVersion,
            ConfigPath = Options.ConfigPath,
            LogPath = Options.LogPath,
            ProtocolVersion = BridgeProtocol.ProtocolVersion,
            SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
            SchemaBundleId = DesktopSidecarProtocol.SchemaBundleId,
            ActiveRequestCount = ActiveRequestCount,
            DegradedSubsystems = DegradedSubsystems,
            LastError = LastError,
        };
    }

    public BridgeHealthFrame CreateHealthFrame(DateTimeOffset? sentAt = null)
    {
        return new BridgeHealthFrame
        {
            SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
            ProcessId = Environment.ProcessId,
            UptimeMs = UptimeMs(sentAt),
            ReadyState = ReadyState,
            AppId = Options.AppId,
            AppVersion = Options.AppVersion,
            ActiveRequestCount = ActiveRequestCount,
            DegradedSubsystems = DegradedSubsystems,
            LastError = LastError,
            SentAt = sentAt ?? DateTimeOffset.UtcNow,
        };
    }
}

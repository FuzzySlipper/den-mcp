namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Local authoritative OperatorSession record used for live local control.
/// This is the .NET app-core model — Electron/webview-specific types must not
/// appear in this file.
/// </summary>
public sealed record OperatorSession
{
    /// <summary>Stable local id within a source instance.</summary>
    public required string SessionId { get; init; }

    /// <summary>Optional display/debug URI such as den-desktop://&lt;source_instance_id&gt;/&lt;session_id&gt;.</summary>
    public string? GlobalRef { get; init; }

    /// <summary>Optional local parent/owner session id.</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>User- or backend-provided title.</summary>
    public string? Title { get; init; }

    /// <summary>Resolved UI name. Defaults from title, command, task, backend, or session id.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Nullable Den correlation.</summary>
    public string? ProjectId { get; init; }

    public long? TaskId { get; init; }

    public string? WorkspaceId { get; init; }

    /// <summary>Current or launch working directory when known.</summary>
    public string? Cwd { get; init; }

    /// <summary>Session kind: terminal, agent, process, artifact_observer, collaboration_delivery, unknown.</summary>
    public string Kind { get; init; } = "unknown";

    /// <summary>Backend: direct_pty, tmux, zellij, process, pi_artifact, external, unknown.</summary>
    public string Backend { get; init; } = "unknown";

    /// <summary>Local-only backend identity, e.g. PTY/process id, tmux socket/session/window/pane.</summary>
    public string? BackendRef { get; init; }

    /// <summary>Normalized status from OperatorSessionStatus constants.</summary>
    public string Status { get; init; } = OperatorSessionStatus.Starting;

    /// <summary>Current command/tool/process summary when known.</summary>
    public string? CurrentCommand { get; init; }

    /// <summary>Optional Pi/agent identity and role.</summary>
    public string? AgentIdentity { get; init; }
    public string? Role { get; init; }

    /// <summary>Structured capability flags plus reasons and constraints.</summary>
    public OperatorSessionCapabilities Capabilities { get; init; } = OperatorSessionCapabilities.Empty();

    /// <summary>Timestamps.</summary>
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? LastObservedAt { get; init; }
    public DateTime? LastActivityAt { get; init; }
    public DateTime? ExitedAt { get; init; }

    /// <summary>
    /// Registry-authoritative mutation time. OperatorSessionRegistry overwrites
    /// this with its local clock on every register/refresh; source-observation
    /// times are preserved separately in LastObservedAt/LastActivityAt.
    /// </summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>Terminal/process exit details when known.</summary>
    public int? ExitCode { get; init; }
    public int? ExitSignal { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>Persisted desktop source identity from settings.</summary>
    public required string SourceInstanceId { get; init; }

    /// <summary>Optional operator-friendly source name.</summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>Lease fields.</summary>
    public string? LeaseId { get; init; }
    public long LeaseGeneration { get; init; }
    public DateTime? LeaseAcquiredAt { get; init; }
    public DateTime? LeaseExpiresAt { get; init; }
    public DateTime? LeaseHeartbeatAt { get; init; }

    /// <summary>Bounded current warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Bounded structured summaries for display; raw stream remains separate.
    /// Read cursors are content-identity cursors over this retained snapshot,
    /// not durable raw-terminal stream cursors.
    /// </summary>
    public IReadOnlyList<OperatorSessionActivityItem> RecentActivity { get; init; } = [];

    /// <summary>Local child session refs and Den run/workspace links when known.</summary>
    public IReadOnlyList<OperatorSessionChildRef> Children { get; init; } = [];

    /// <summary>Monotonic local event/snapshot sequence for reconnect/gap detection.</summary>
    public long Sequence { get; init; }
}

public sealed record OperatorSessionCapabilities
{
    public bool CanAttach { get; init; }
    public bool CanDetach { get; init; }
    public bool CanSendInput { get; init; }
    public bool CanResize { get; init; }
    public bool CanTerminate { get; init; }
    public bool CanKill { get; init; }
    public bool CanReconnect { get; init; }
    public bool CanFocus { get; init; }
    public bool CanOpenExternalAttach { get; init; }
    public bool CanReadActivity { get; init; }
    public bool CanStreamTerminal { get; init; }
    public bool CanDeliverCompiledResponse { get; init; }

    /// <summary>Reason for disabled capabilities.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether confirmation is needed for destructive actions.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>Whether a lease is required for control.</summary>
    public bool LeaseRequired { get; init; }

    /// <summary>Backend-specific constraints as JSON.</summary>
    public string? Constraints { get; init; }

    public static OperatorSessionCapabilities Empty() => new();

    public static OperatorSessionCapabilities ObserveOnly(string reason, bool canReadActivity = false)
    {
        return new OperatorSessionCapabilities
        {
            CanReadActivity = canReadActivity,
            Reason = reason,
        };
    }

    public static OperatorSessionCapabilities FullControl(string? reason = null)
    {
        return new OperatorSessionCapabilities
        {
            CanAttach = true,
            CanDetach = true,
            CanSendInput = true,
            CanResize = true,
            CanTerminate = true,
            CanKill = true,
            CanReconnect = true,
            CanFocus = true,
            CanOpenExternalAttach = true,
            CanReadActivity = true,
            CanStreamTerminal = true,
            CanDeliverCompiledResponse = false, // authority-gated
            Reason = reason,
        };
    }
}

public static class OperatorSessionStatus
{
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Idle = "idle";
    public const string Exited = "exited";
    public const string Failed = "failed";
    public const string Detached = "detached";
    public const string Stale = "stale";
    public const string SourceOffline = "source_offline";
    public const string Crashed = "crashed";

    /// <summary>
    /// Map a legacy current_phase value to a normalized status.
    /// </summary>
    public static string FromLegacyPhase(string? phase, DateTime? endedAt = null)
    {
        return phase switch
        {
            "complete" => Exited,
            "running" => Running,
            "working" or "coding" or "tool_use" => Running,
            "failed" => Failed,
            _ when endedAt is not null => Exited,
            _ => Running,
        };
    }
}

public static class OperatorSessionKind
{
    public const string Terminal = "terminal";
    public const string Agent = "agent";
    public const string Process = "process";
    public const string ArtifactObserver = "artifact_observer";
    public const string CollaborationDelivery = "collaboration_delivery";
    public const string Unknown = "unknown";
}

public static class OperatorSessionBackend
{
    public const string DirectPty = "direct_pty";
    public const string Tmux = "tmux";
    public const string Zellij = "zellij";
    public const string Process = "process";
    public const string PiArtifact = "pi_artifact";
    public const string External = "external";
    public const string Unknown = "unknown";
}

public sealed record OperatorSessionActivityItem
{
    public string? Kind { get; init; }
    public string? Role { get; init; }
    public string? Tool { get; init; }
    public string? Summary { get; init; }
    public string? Timestamp { get; init; }
}

public sealed record OperatorSessionChildRef
{
    public string SessionId { get; init; } = string.Empty;
    public string? Kind { get; init; }
    public string? Title { get; init; }
}

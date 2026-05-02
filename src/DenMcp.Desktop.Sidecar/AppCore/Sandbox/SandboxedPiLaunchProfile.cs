using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Typed app-core launch profile for sandboxed Pi sessions. All fields are
/// validated and allow-listed by the app-core; the renderer never supplies
/// shell strings or arbitrary dispatch commands.
///
/// Design follows #910 research and #1073 hardening:
/// - R910-1: Pi config/auth directory strategy is explicit.
///   <see cref="PiConfigStrategy"/> defaults to <see cref="PiConfigStrategies.DedicatedPerRun"/>
///   instead of host bind-rw. <see cref="PiConfigStrategies.HostBindRw"/> is only
///   permitted with an explicit capability warning and debt metadata.
/// - R910-2: OAuth callback port strategy is allow-listed and host-loopback-only.
/// </summary>
public sealed record SandboxedPiLaunchProfile
{
    /// <summary>Profile id — deterministic from project/task/workspace, not user-supplied.</summary>
    [JsonPropertyName("profile_id")]
    public required string ProfileId { get; init; }

    // ── Den correlation ──────────────────────────────────────────────────

    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    // ── Sandbox configuration ────────────────────────────────────────────

    /// <summary>Sandbox kind. Only <see cref="SandboxKinds.DockerComposePi"/> is supported in v1.</summary>
    [JsonPropertyName("sandbox_kind")]
    public string SandboxKind { get; init; } = SandboxKinds.DockerComposePi;

    /// <summary>Allow-listed compose file path. Must match a configured sandbox profile entry.</summary>
    [JsonPropertyName("compose_file")]
    public required string ComposeFile { get; init; }

    /// <summary>Allow-listed Docker Compose service name.</summary>
    [JsonPropertyName("service")]
    public string Service { get; init; } = SandboxDefaults.Service;

    /// <summary>Host dev directory that will be bind-mounted. Must be validated as a real directory.</summary>
    [JsonPropertyName("dev_dir")]
    public required string DevDir { get; init; }

    /// <summary>
    /// Container working directory. Derived from <see cref="DevDir"/> + project relative path
    /// by the builder; not user-supplied.
    /// </summary>
    [JsonPropertyName("container_workdir")]
    public string? ContainerWorkdir { get; init; }

    /// <summary>Tmux session name prefix.</summary>
    [JsonPropertyName("session_prefix")]
    public string SessionPrefix { get; init; } = SandboxDefaults.SessionPrefix;

    // ── Pi config / auth directory strategy (R910-1) ─────────────────────

    /// <summary>
    /// Strategy for Pi configuration and auth directory inside the sandbox.
    /// Default is <see cref="PiConfigStrategies.DedicatedPerRun"/>.
    /// </summary>
    [JsonPropertyName("pi_config_strategy")]
    public string PiConfigStrategy { get; init; } = PiConfigStrategies.DedicatedPerRun;

    /// <summary>
    /// Host path to a dedicated per-run Pi config directory when strategy is
    /// <see cref="PiConfigStrategies.DedicatedPerRun"/>. Created and managed by app-core.
    /// </summary>
    [JsonPropertyName("pi_config_dir")]
    public string? PiConfigDir { get; init; }

    /// <summary>
    /// Explicit capability warnings when the chosen strategy carries known risks.
    /// For <see cref="PiConfigStrategies.HostBindRw"/>, this must include at least
    /// one warning about host auth/settings exposure.
    /// </summary>
    [JsonPropertyName("capability_warnings")]
    public IReadOnlyList<string> CapabilityWarnings { get; init; } = [];

    /// <summary>
    /// Technical debt metadata when a non-preferred strategy is selected.
    /// Required when <see cref="PiConfigStrategy"/> is
    /// <see cref="PiConfigStrategies.HostBindRw"/>.
    /// </summary>
    [JsonPropertyName("debt_metadata")]
    public SandboxedPiLaunchDebtMetadata? DebtMetadata { get; init; }

    // ── OAuth callback port strategy (R910-2) ────────────────────────────

    /// <summary>
    /// OAuth callback port configuration. Ports are allow-listed and bound to
    /// host loopback (127.0.0.1) only.
    /// </summary>
    [JsonPropertyName("oauth_port_config")]
    public SandboxedPiOAuthPortConfig OAuthPortConfig { get; init; } = new() { Strategy = OAuthPortStrategies.AllowListed };

    // ── Credential mounts ────────────────────────────────────────────────

    /// <summary>
    /// Credential mounts. Each entry must match an allow-listed mount kind.
    /// All mounts are read-only in v1.
    /// </summary>
    [JsonPropertyName("credential_mounts")]
    public IReadOnlyList<string> CredentialMounts { get; init; } = SandboxDefaults.DefaultCredentialMounts;

    // ── Pi launch configuration ──────────────────────────────────────────

    /// <summary>
    /// Pi launch mode. Only <see cref="PiLaunchModes.InteractiveCli"/> is supported in v1.
    /// </summary>
    [JsonPropertyName("pi_launch_mode")]
    public string PiLaunchMode { get; init; } = PiLaunchModes.InteractiveCli;

    /// <summary>
    /// Tool profile: which tools the sandboxed Pi session is allowed to use.
    /// Allow-listed by app-core; not user-supplied free text.
    /// </summary>
    [JsonPropertyName("tool_profile")]
    public string ToolProfile { get; init; } = ToolProfiles.Coding;

    /// <summary>Optional model override.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Optional provider override.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    // ── Network profile ──────────────────────────────────────────────────

    /// <summary>Network profile. <see cref="NetworkProfiles.Unrestricted"/> is the v1 default with a warning.</summary>
    [JsonPropertyName("network_profile")]
    public string NetworkProfile { get; init; } = NetworkProfiles.Unrestricted;
}

/// <summary>
/// Technical debt metadata when a non-preferred Pi config strategy is selected.
/// </summary>
public sealed record SandboxedPiLaunchDebtMetadata
{
    /// <summary>Why the non-preferred strategy was selected.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>Tracking issue or task id for resolution.</summary>
    [JsonPropertyName("tracking_task_id")]
    public long? TrackingTaskId { get; init; }

    /// <summary>When the debt was accepted.</summary>
    [JsonPropertyName("accepted_at")]
    public string? AcceptedAt { get; init; }
}

/// <summary>
/// OAuth callback port configuration for sandboxed Pi sessions (R910-2).
/// Ports are allow-listed, host-loopback-only, and never auto-published
/// to all interfaces.
/// </summary>
public sealed record SandboxedPiOAuthPortConfig
{
    /// <summary>
    /// Port handling strategy.
    /// <list type="bullet">
    ///   <item><see cref="OAuthPortStrategies.AllowListed"/>: Publish specific allow-listed ports on 127.0.0.1.</item>
    ///   <item><see cref="OAuthPortStrategies.ManualFallback"/>: No ports published; user handles OAuth externally.</item>
    ///   <item><see cref="OAuthPortStrategies.Disabled"/>: No OAuth callback needed (e.g. pre-authenticated config).</item>
    /// </list>
    /// </summary>
    [JsonPropertyName("strategy")]
    public string Strategy { get; init; } = OAuthPortStrategies.AllowListed;

    /// <summary>
    /// Allow-listed host loopback ports to publish. Only used when strategy is
    /// <see cref="OAuthPortStrategies.AllowListed"/>. All ports bind to 127.0.0.1 only.
    /// Default: <see cref="SandboxDefaults.OAuthCallbackPorts"/>.
    /// </summary>
    [JsonPropertyName("allowed_ports")]
    public IReadOnlyList<int> AllowedPorts { get; init; } = SandboxDefaults.OAuthCallbackPorts;

    /// <summary>
    /// Bind address for published ports. Must be 127.0.0.1 (loopback only).
    /// </summary>
    [JsonPropertyName("bind_address")]
    public string BindAddress { get; init; } = SandboxDefaults.LoopbackAddress;

    /// <summary>
    /// When strategy is <see cref="OAuthPortStrategies.ManualFallback"/>,
    /// this contains instructions for the operator.
    /// </summary>
    [JsonPropertyName("manual_fallback_instructions")]
    public string? ManualFallbackInstructions { get; init; }
}

// ── Strategy / kind constants ────────────────────────────────────────────

/// <summary>Constants for <see cref="SandboxedPiLaunchProfile.SandboxKind"/>.</summary>
public static class SandboxKinds
{
    public const string DockerComposePi = "docker_compose_pi";
}

/// <summary>Constants for <see cref="SandboxedPiLaunchProfile.PiConfigStrategy"/> (R910-1).</summary>
public static class PiConfigStrategies
{
    /// <summary>
    /// Create a dedicated per-run Pi config directory (preferred).
    /// The sandbox gets its own fresh config/auth namespace — no host ~/.pi exposure.
    /// </summary>
    public const string DedicatedPerRun = "dedicated_per_run";

    /// <summary>
    /// Bind-mount host ~/.pi read-write into the sandbox (not preferred).
    /// Requires <see cref="SandboxedPiLaunchProfile.CapabilityWarnings"/> and
    /// <see cref="SandboxedPiLaunchProfile.DebtMetadata"/> to be set.
    /// </summary>
    public const string HostBindRw = "host_bind_rw";
}

/// <summary>Constants for <see cref="SandboxedPiOAuthPortConfig.Strategy"/> (R910-2).</summary>
public static class OAuthPortStrategies
{
    /// <summary>Publish specific allow-listed ports on 127.0.0.1.</summary>
    public const string AllowListed = "allow_listed";

    /// <summary>No ports published; user handles OAuth externally.</summary>
    public const string ManualFallback = "manual_fallback";

    /// <summary>No OAuth callback needed.</summary>
    public const string Disabled = "disabled";
}

/// <summary>Constants for <see cref="SandboxedPiLaunchProfile.PiLaunchMode"/>.</summary>
public static class PiLaunchModes
{
    public const string InteractiveCli = "interactive_cli";
}

/// <summary>Allow-listed tool profiles for sandboxed Pi sessions.</summary>
public static class ToolProfiles
{
    /// <summary>Full coding tool access: read, bash, edit, write.</summary>
    public const string Coding = "coding";

    /// <summary>Read-only tools: read, bash (non-destructive review).</summary>
    public const string ReadOnly = "read_only";

    /// <summary>No tools — planning/text only.</summary>
    public const string NoTools = "no_tools";

    /// <summary>Valid tool profiles.</summary>
    public static readonly IReadOnlySet<string> ValidProfiles = new HashSet<string>([Coding, ReadOnly, NoTools], StringComparer.Ordinal);

    /// <summary>Map a tool profile to Pi CLI args. App-core owns this mapping.</summary>
    public static IReadOnlyList<string> ToPiArgs(string toolProfile)
    {
        return toolProfile switch
        {
            Coding => ["--tools", "read,bash,edit,write"],
            ReadOnly => ["--tools", "read,bash"],
            NoTools => ["--no-tools"],
            _ => ["--no-tools"],
        };
    }
}

/// <summary>Network profile constants.</summary>
public static class NetworkProfiles
{
    public const string Unrestricted = "unrestricted";
    public const string DenOnly = "den_only";
    public const string Offline = "offline";
}

/// <summary>Allow-listed credential mount kinds.</summary>
public static class CredentialMountKinds
{
    public const string GitConfig = "gitconfig";
    public const string Ssh = "ssh";
    public const string Gh = "gh";

    /// <summary>Valid credential mount kinds (all read-only in v1).</summary>
    public static readonly IReadOnlySet<string> ValidKinds = new HashSet<string>([GitConfig, Ssh, Gh], StringComparer.Ordinal);
}

/// <summary>Default values for sandbox configuration.</summary>
public static class SandboxDefaults
{
    public const string Service = "sandbox";
    public const string SessionPrefix = "den-pi";
    public const string LoopbackAddress = "127.0.0.1";

    /// <summary>Default OAuth callback ports (Pi OAuth subscription flow).</summary>
    public static readonly IReadOnlyList<int> OAuthCallbackPorts = [3000, 3001, 8080];

    /// <summary>Default credential mounts (all read-only in v1).</summary>
    public static readonly IReadOnlyList<string> DefaultCredentialMounts = [CredentialMountKinds.GitConfig, CredentialMountKinds.Ssh, CredentialMountKinds.Gh];

    /// <summary>Default container mount point for dev directory.</summary>
    public const string ContainerDevDir = "/home/pi/dev";

    /// <summary>Default container mount point for Pi config.</summary>
    public const string ContainerPiConfigDir = "/home/pi/.pi";
}

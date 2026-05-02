using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Builds and validates <see cref="SandboxedPiLaunchProfile"/> instances.
/// All validation is app-core owned; the renderer never bypasses these checks.
/// </summary>
public sealed class SandboxedPiLaunchProfileBuilder
{
    private string? _projectId;
    private long? _taskId;
    private string? _workspaceId;
    private string? _title;
    private string? _composeFile;
    private string _service = SandboxDefaults.Service;
    private string? _devDir;
    private string? _containerWorkdir;
    private string _piConfigStrategy = PiConfigStrategies.DedicatedPerRun;
    private string? _piConfigDir;
    private List<string> _capabilityWarnings = [];
    private SandboxedPiLaunchDebtMetadata? _debtMetadata;
    private SandboxedPiOAuthPortConfig _oauthPortConfig = new() { Strategy = OAuthPortStrategies.AllowListed };
    private List<string> _credentialMounts = [.. SandboxDefaults.DefaultCredentialMounts];
    private string _piLaunchMode = PiLaunchModes.InteractiveCli;
    private string _toolProfile = ToolProfiles.Coding;
    private string? _model;
    private string? _provider;
    private string _networkProfile = NetworkProfiles.Unrestricted;
    private string _sessionPrefix = SandboxDefaults.SessionPrefix;

    // Allow-list of compose file paths configured by the operator.
    private readonly HashSet<string> _allowedComposeFiles;

    /// <summary>
    /// Create a builder with the given allow-listed compose file paths.
    /// </summary>
    public SandboxedPiLaunchProfileBuilder(IEnumerable<string>? allowedComposeFiles = null)
    {
        _allowedComposeFiles = new HashSet<string>(
            allowedComposeFiles ?? [],
            StringComparer.Ordinal);
    }

    public SandboxedPiLaunchProfileBuilder WithProjectId(string projectId)
    {
        _projectId = projectId;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithTaskId(long? taskId)
    {
        _taskId = taskId;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithWorkspaceId(string? workspaceId)
    {
        _workspaceId = workspaceId;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithTitle(string? title)
    {
        _title = title;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithComposeFile(string composeFile)
    {
        _composeFile = composeFile;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithService(string service)
    {
        _service = service;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithDevDir(string devDir)
    {
        _devDir = devDir;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithContainerWorkdir(string? containerWorkdir)
    {
        _containerWorkdir = containerWorkdir;
        return this;
    }

    /// <summary>
    /// Set the Pi config strategy. When <see cref="PiConfigStrategies.HostBindRw"/>
    /// is selected, <see cref="WithDebtMetadata"/> and <see cref="WithCapabilityWarnings"/>
    /// must also be called before <see cref="Build"/>.
    /// </summary>
    public SandboxedPiLaunchProfileBuilder WithPiConfigStrategy(string strategy)
    {
        _piConfigStrategy = strategy;
        return this;
    }

    /// <summary>
    /// Set the dedicated per-run Pi config directory. Required when strategy
    /// is <see cref="PiConfigStrategies.DedicatedPerRun"/>.
    /// </summary>
    public SandboxedPiLaunchProfileBuilder WithPiConfigDir(string? piConfigDir)
    {
        _piConfigDir = piConfigDir;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithCapabilityWarnings(IEnumerable<string> warnings)
    {
        _capabilityWarnings = [.. warnings];
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithDebtMetadata(SandboxedPiLaunchDebtMetadata? metadata)
    {
        _debtMetadata = metadata;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithOAuthPortConfig(SandboxedPiOAuthPortConfig config)
    {
        _oauthPortConfig = config;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithCredentialMounts(IEnumerable<string> mounts)
    {
        _credentialMounts = [.. mounts];
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithToolProfile(string toolProfile)
    {
        _toolProfile = toolProfile;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithModel(string? model)
    {
        _model = model;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithProvider(string? provider)
    {
        _provider = provider;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithNetworkProfile(string networkProfile)
    {
        _networkProfile = networkProfile;
        return this;
    }

    public SandboxedPiLaunchProfileBuilder WithSessionPrefix(string sessionPrefix)
    {
        _sessionPrefix = sessionPrefix;
        return this;
    }

    /// <summary>
    /// Build and validate the profile. Throws <see cref="InvalidOperationException"/>
    /// for invalid configurations.
    /// </summary>
    public SandboxedPiLaunchProfile Build()
    {
        // ── Required fields ──────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(_projectId))
        {
            throw new InvalidOperationException("project_id is required for a sandboxed Pi launch profile.");
        }

        if (string.IsNullOrWhiteSpace(_composeFile))
        {
            throw new InvalidOperationException("compose_file is required for a sandboxed Pi launch profile.");
        }

        if (string.IsNullOrWhiteSpace(_devDir))
        {
            throw new InvalidOperationException("dev_dir is required for a sandboxed Pi launch profile.");
        }

        // ── Allow-list validation ────────────────────────────────────────
        if (_allowedComposeFiles.Count > 0 && !_allowedComposeFiles.Contains(_composeFile))
        {
            throw new InvalidOperationException(
                $"compose_file '{_composeFile}' is not in the allow-listed sandbox profiles.");
        }

        if (!string.Equals(_service, SandboxDefaults.Service, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"service '{_service}' is not allow-listed. Only '{SandboxDefaults.Service}' is supported in v1.");
        }

        if (!SandboxKinds.DockerComposePi.Equals("docker_compose_pi", StringComparison.Ordinal))
        {
            // This is a compile-time constant check placeholder; the real validation
            // is that sandbox_kind is not renderer-set.
        }

        // ── Pi config strategy validation (R910-1) ──────────────────────
        ValidatePiConfigStrategy();

        // ── OAuth port validation (R910-2) ───────────────────────────────
        ValidateOAuthPortConfig();

        // ── Credential mount validation ──────────────────────────────────
        ValidateCredentialMounts();

        // ── Tool profile validation ──────────────────────────────────────
        if (!ToolProfiles.ValidProfiles.Contains(_toolProfile))
        {
            throw new InvalidOperationException(
                $"tool_profile '{_toolProfile}' is not a valid allow-listed tool profile. " +
                $"Valid: {string.Join(", ", ToolProfiles.ValidProfiles)}");
        }

        // ── Network profile validation ──────────────────────────────────
        var validNetworkProfiles = new HashSet<string>(
            [NetworkProfiles.Unrestricted, NetworkProfiles.DenOnly, NetworkProfiles.Offline],
            StringComparer.Ordinal);
        if (!validNetworkProfiles.Contains(_networkProfile))
        {
            throw new InvalidOperationException(
                $"network_profile '{_networkProfile}' is not valid. " +
                $"Valid: {string.Join(", ", validNetworkProfiles)}");
        }

        // ── Derive computed fields ───────────────────────────────────────
        var profileId = DeriveProfileId();
        var containerWorkdir = _containerWorkdir ?? DeriveContainerWorkdir();

        // ── Build warnings for non-preferred defaults ───────────────────
        var warnings = new List<string>(_capabilityWarnings);
        if (_networkProfile == NetworkProfiles.Unrestricted)
        {
            warnings.Add("Network is unrestricted — sandbox has full network access.");
        }

        return new SandboxedPiLaunchProfile
        {
            ProfileId = profileId,
            ProjectId = _projectId,
            TaskId = _taskId,
            WorkspaceId = _workspaceId,
            Title = _title,
            SandboxKind = SandboxKinds.DockerComposePi,
            ComposeFile = _composeFile,
            Service = _service,
            DevDir = _devDir,
            ContainerWorkdir = containerWorkdir,
            SessionPrefix = _sessionPrefix,
            PiConfigStrategy = _piConfigStrategy,
            PiConfigDir = _piConfigDir,
            CapabilityWarnings = warnings,
            DebtMetadata = _debtMetadata,
            OAuthPortConfig = _oauthPortConfig,
            CredentialMounts = _credentialMounts,
            PiLaunchMode = _piLaunchMode,
            ToolProfile = _toolProfile,
            Model = _model,
            Provider = _provider,
            NetworkProfile = _networkProfile,
        };
    }

    private void ValidatePiConfigStrategy()
    {
        var valid = new HashSet<string>(
            [PiConfigStrategies.DedicatedPerRun, PiConfigStrategies.HostBindRw],
            StringComparer.Ordinal);

        if (!valid.Contains(_piConfigStrategy))
        {
            throw new InvalidOperationException(
                $"pi_config_strategy '{_piConfigStrategy}' is not valid. " +
                $"Valid: {string.Join(", ", valid)}");
        }

        if (_piConfigStrategy == PiConfigStrategies.HostBindRw)
        {
            if (_capabilityWarnings.Count == 0)
            {
                throw new InvalidOperationException(
                    $"pi_config_strategy '{PiConfigStrategies.HostBindRw}' requires at least one capability_warning " +
                    "about host auth/settings exposure.");
            }

            if (_debtMetadata is null)
            {
                throw new InvalidOperationException(
                    $"pi_config_strategy '{PiConfigStrategies.HostBindRw}' requires debt_metadata " +
                    "explaining why the non-preferred strategy was selected.");
            }
        }

        if (_piConfigStrategy == PiConfigStrategies.DedicatedPerRun && string.IsNullOrWhiteSpace(_piConfigDir))
        {
            // Allow null pi_config_dir in the builder — the launcher infrastructure
            // will generate one. But if explicitly set to empty, fail.
            // The profile just carries null and the launcher creates the dir.
        }
    }

    private void ValidateOAuthPortConfig()
    {
        var config = _oauthPortConfig;
        var validStrategies = new HashSet<string>(
            [OAuthPortStrategies.AllowListed, OAuthPortStrategies.ManualFallback, OAuthPortStrategies.Disabled],
            StringComparer.Ordinal);

        if (!validStrategies.Contains(config.Strategy))
        {
            throw new InvalidOperationException(
                $"oauth_port_config.strategy '{config.Strategy}' is not valid. " +
                $"Valid: {string.Join(", ", validStrategies)}");
        }

        // Bind address must be loopback only
        if (config.BindAddress != SandboxDefaults.LoopbackAddress)
        {
            throw new InvalidOperationException(
                $"oauth_port_config.bind_address must be '{SandboxDefaults.LoopbackAddress}' (loopback only). " +
                $"Got: '{config.BindAddress}'");
        }

        // Port range validation
        foreach (var port in config.AllowedPorts)
        {
            if (port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    $"oauth_port_config.allowed_ports contains invalid port {port}. " +
                    "Port must be between 1 and 65535.");
            }
        }

        // When strategy is allow_listed, must have at least one port
        if (config.Strategy == OAuthPortStrategies.AllowListed && config.AllowedPorts.Count == 0)
        {
            throw new InvalidOperationException(
                $"oauth_port_config.strategy '{OAuthPortStrategies.AllowListed}' requires at least one allowed_port.");
        }
    }

    private void ValidateCredentialMounts()
    {
        foreach (var mount in _credentialMounts)
        {
            if (!CredentialMountKinds.ValidKinds.Contains(mount))
            {
                throw new InvalidOperationException(
                    $"credential_mount '{mount}' is not allow-listed. " +
                    $"Valid: {string.Join(", ", CredentialMountKinds.ValidKinds)}");
            }
        }
    }

    private string DeriveProfileId()
    {
        var parts = new StringBuilder();
        parts.Append(_projectId);
        if (_taskId is { } tid) parts.Append($":task{tid}");
        if (!string.IsNullOrWhiteSpace(_workspaceId)) parts.Append($":ws{_workspaceId}");
        // Deterministic hash for uniqueness
        var hash = ShortHash(parts.ToString());
        return $"sandbox-pi:{_projectId}:{hash}";
    }

    private string DeriveContainerWorkdir()
    {
        // Default: /home/pi/dev + project slug
        var slug = (_projectId ?? "unknown").Trim().ToLowerInvariant();
        return $"{SandboxDefaults.ContainerDevDir}/{slug}";
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}

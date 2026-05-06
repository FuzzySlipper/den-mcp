namespace DenMcp.Core.Models;

/// <summary>
/// Server-owned configuration for rendering a Docker Compose based Pi launch profile.
/// This is a launch-profile contract only; process lifecycle is owned by later Pi
/// session host work.
/// </summary>
public sealed class PiDockerLaunchProfileOptions
{
    public string ComposeFile { get; set; } = "/home/patch/dev/linux/pi-docker/compose.yaml";
    public string Service { get; set; } = "pi";
    public string DevDir { get; set; } = "~/dev";
    public string PiStateRootDir { get; set; } = "~/.local/share/den-mcp/pi-sessions";
    public string Image { get; set; } = "pi-sandbox:latest";
    public string PiVersion { get; set; } = "0.71.0";
    public string NodeVersion { get; set; } = "22";
    public int SandboxUid { get; set; } = 1000;
    public int SandboxGid { get; set; } = 1000;
    public string? GitConfigPath { get; set; }
    public string? SshDir { get; set; }
    public string? GhConfigDir { get; set; }
    public string CredentialFallbackRootDir { get; set; } = "~/.local/share/den-mcp/pi-credential-fallbacks";
    public string HostCallbackBindAddress { get; set; } = PiDockerLaunchProfileDefaults.LoopbackAddress;
    public string? HostId { get; set; }
    public string TmuxExecutable { get; set; } = "tmux";
    public string DockerExecutable { get; set; } = "docker";
    public List<int> DefaultCallbackContainerPorts { get; set; } = [.. PiDockerLaunchProfileDefaults.CallbackContainerPorts];
}

public sealed class PiDockerLaunchRenderRequest
{
    public required string ProjectId { get; init; }
    public required string SessionId { get; init; }
    public long? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Title { get; init; }
    public string? DevDir { get; init; }
    public string? PiStateDir { get; init; }
    public string? ComposeFile { get; init; }
    public string? Service { get; init; }
    public string? Image { get; init; }
    public string? PiVersion { get; init; }
    public string? NodeVersion { get; init; }
    public string? GitConfigPath { get; init; }
    public string? SshDir { get; init; }
    public string? GhConfigDir { get; init; }
    public IReadOnlyList<PiDockerCallbackPort> CallbackPorts { get; init; } = [];
}

public sealed class PiDockerCallbackPort
{
    public int HostPort { get; init; }
    public int ContainerPort { get; init; }
    public string BindAddress { get; init; } = PiDockerLaunchProfileDefaults.LoopbackAddress;
}

public sealed class PiDockerLaunchProfile
{
    public required string ProfileId { get; init; }
    public required string ProjectId { get; init; }
    public required string SessionId { get; init; }
    public long? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Title { get; init; }
    public required string ComposeProjectName { get; init; }
    public required string ComposeFile { get; init; }
    public required string Service { get; init; }
    public required string DevDir { get; init; }
    public required string PiStateDir { get; init; }
    public required string Image { get; init; }
    public required string PiVersion { get; init; }
    public required string NodeVersion { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<PiDockerVolumeMount> VolumeMounts { get; init; } = [];
    public IReadOnlyList<PiDockerCallbackPort> CallbackPorts { get; init; } = [];
    public IReadOnlyList<string> DockerComposeConfigArgs { get; init; } = [];
    public IReadOnlyList<string> DockerComposeBuildArgs { get; init; } = [];
    public IReadOnlyList<string> DockerComposeRunArgs { get; init; } = [];
    public IReadOnlyList<string> CacheVolumeNames { get; init; } = [];
    public IReadOnlyList<string> RequiredHostPaths { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> KnownLimitations { get; init; } = [];
}

public sealed class PiDockerVolumeMount
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public bool ReadOnly { get; init; }
    public required string Purpose { get; init; }
}

public static class PiDockerLaunchProfileDefaults
{
    public const string LoopbackAddress = "127.0.0.1";
    public const string ContainerDevDir = "/home/pi/dev";
    public const string ContainerPiStateDir = "/home/pi/.pi";
    public const string ContainerGitConfigPath = "/home/pi/.gitconfig";
    public const string ContainerSshDir = "/home/pi/.ssh";
    public const string ContainerGhConfigDir = "/home/pi/.config/gh";
    public const string CacheVolume = "pi-cache";
    public const string NpmCacheVolume = "pi-npm-cache";

    /// <summary>Callback ports preserved from the inspected pi-docker compose service.</summary>
    public static readonly IReadOnlyList<int> CallbackContainerPorts = [1455, 53692, 8085, 51121];
}

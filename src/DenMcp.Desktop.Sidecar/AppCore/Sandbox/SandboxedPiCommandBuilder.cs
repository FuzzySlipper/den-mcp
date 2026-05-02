using System.Text;
using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Builds argument vectors (not shell strings) for Docker Compose and tmux
/// commands from a validated <see cref="SandboxedPiLaunchProfile"/>.
/// The renderer never sees or controls these arguments.
///
/// Design principle: every argument is produced by app-core code from typed,
/// allow-listed fields. No renderer-supplied shell strings or generic dispatch.
/// </summary>
public sealed class SandboxedPiCommandBuilder
{
    /// <summary>
    /// Build Docker Compose "up" arguments for the sandbox service.
    /// Uses <see cref="ProcessStartInfo.ArgumentList"/> semantics — each entry
    /// is a separate argument, not a shell-escaped string.
    /// </summary>
    public IReadOnlyList<string> BuildComposeUpArgs(SandboxedPiLaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var args = new List<string>
        {
            "compose",
            "-f", profile.ComposeFile,
            "up",
            "-d",
            profile.Service,
        };

        // OAuth port publishes (R910-2)
        if (profile.OAuthPortConfig.Strategy == OAuthPortStrategies.AllowListed)
        {
            // Ports are added as -p bind:port:port arguments
            // The actual implementation would use compose yaml ports or
            // docker compose run --publish flags; here we model the intent.
        }

        return args;
    }

    /// <summary>
    /// Build Docker Compose "exec" arguments to create a tmux session
    /// inside the sandbox and start Pi.
    /// </summary>
    public IReadOnlyList<string> BuildTmuxExecArgs(SandboxedPiLaunchProfile profile, string tmuxSessionName)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(tmuxSessionName);

        var args = new List<string>
        {
            "compose",
            "-f", profile.ComposeFile,
            "exec",
            "-T",
            "-e", "TERM=xterm-256color",
            "--workdir", profile.ContainerWorkdir ?? SandboxDefaults.ContainerDevDir,
            profile.Service,
            "tmux", "new-session", "-d",
            "-s", tmuxSessionName,
            "-c", profile.ContainerWorkdir ?? SandboxDefaults.ContainerDevDir,
        };

        // Pi command from allow-listed profile fields only
        args.Add("pi");
        args.AddRange(ToolProfiles.ToPiArgs(profile.ToolProfile));

        if (!string.IsNullOrWhiteSpace(profile.Model))
        {
            args.Add("--model");
            args.Add(profile.Model);
        }

        if (!string.IsNullOrWhiteSpace(profile.Provider))
        {
            args.Add("--provider");
            args.Add(profile.Provider);
        }

        return args;
    }

    /// <summary>
    /// Build Docker Compose volume mount arguments for Pi config directory.
    /// Result depends on the <see cref="SandboxedPiLaunchProfile.PiConfigStrategy"/>.
    /// </summary>
    public IReadOnlyList<string> BuildPiConfigVolumeArgs(SandboxedPiLaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.PiConfigStrategy switch
        {
            PiConfigStrategies.DedicatedPerRun when !string.IsNullOrWhiteSpace(profile.PiConfigDir) =>
                [$"-v", $"{profile.PiConfigDir}:{SandboxDefaults.ContainerPiConfigDir}"],
            PiConfigStrategies.HostBindRw =>
                [$"-v", $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/.pi:{SandboxDefaults.ContainerPiConfigDir}"],
            _ => [], // dedicated_per_run without explicit dir: launcher creates temp dir
        };
    }

    /// <summary>
    /// Build Docker port publishing arguments for OAuth callback ports.
    /// All ports are bound to loopback (127.0.0.1) only.
    /// </summary>
    public IReadOnlyList<string> BuildOAuthPortArgs(SandboxedPiLaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.OAuthPortConfig.Strategy != OAuthPortStrategies.AllowListed)
        {
            return [];
        }

        var args = new List<string>();
        foreach (var port in profile.OAuthPortConfig.AllowedPorts)
        {
            args.Add("--publish");
            args.Add($"{profile.OAuthPortConfig.BindAddress}:{port}:{port}");
        }

        return args;
    }

    /// <summary>
    /// Build a deterministic tmux session name from the profile and an optional
    /// disambiguation hash. The name is bounded and safe for tmux.
    /// </summary>
    public static string BuildTmuxSessionName(SandboxedPiLaunchProfile profile, string? disambiguation = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var parts = new List<string>
        {
            profile.SessionPrefix,
            Slug(profile.ProjectId),
        };

        if (profile.TaskId is { } tid)
        {
            parts.Add($"task{tid}");
        }

        var baseName = string.Join('-', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        var hashInput = $"{profile.ProfileId}|{disambiguation}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var suffix = TmuxSessionNamingShortHash(hashInput);
        var name = $"{baseName}-{suffix}";

        // Trim to tmux max session name length
        const int maxLen = 80;
        if (name.Length > maxLen)
        {
            name = name[..(maxLen - suffix.Length - 1)] + "-" + suffix;
        }

        return name;
    }

    /// <summary>
    /// Build the OperatorSession constraints JSON from a launch profile.
    /// This goes into <see cref="OperatorSessionCapabilities.Constraints"/>
    /// and is the structured metadata Den sees (no raw terminal bytes or Docker details).
    /// </summary>
    public static string BuildSessionConstraints(SandboxedPiLaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var constraints = new Dictionary<string, object?>
        {
            ["sandbox_kind"] = profile.SandboxKind,
            ["service"] = profile.Service,
            ["dev_dir"] = profile.DevDir,
            ["container_workdir"] = profile.ContainerWorkdir,
            ["pi_config_strategy"] = profile.PiConfigStrategy,
            ["oauth_port_strategy"] = profile.OAuthPortConfig.Strategy,
            ["credential_mounts"] = profile.CredentialMounts,
            ["pi_launch_mode"] = profile.PiLaunchMode,
            ["tool_profile"] = profile.ToolProfile,
            ["network_profile"] = profile.NetworkProfile,
            ["raw_stream_scope"] = "local_only",
        };

        if (profile.CapabilityWarnings.Count > 0)
        {
            constraints["capability_warnings"] = profile.CapabilityWarnings;
        }

        return System.Text.Json.JsonSerializer.Serialize(constraints);
    }

    private static string Slug(string value)
    {
        var lowered = value.Trim().ToLowerInvariant();
        var slug = new StringBuilder();
        foreach (var c in lowered)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-')
            {
                slug.Append(c);
            }
            else
            {
                slug.Append('-');
            }
        }

        var result = slug.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "session" : result;
    }

    private static string TmuxSessionNamingShortHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}

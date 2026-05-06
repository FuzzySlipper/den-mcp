using System.Security.Cryptography;
using System.Text;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public sealed class PiSessionLaunchPlan
{
    public required PiSessionRecord Record { get; init; }
    public required PiDockerLaunchProfile LaunchProfile { get; init; }
    public IReadOnlyList<string> LaunchCommand { get; init; } = [];
}

public sealed class PiSessionHostLaunchResult
{
    public required string State { get; init; }
    public string? StateReason { get; init; }
    public string? ContainerId { get; init; }
    public string? ContainerName { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? LastActivityAt { get; init; }
}

public sealed class PiSessionHostStatus
{
    public required string State { get; init; }
    public string? StateReason { get; init; }
    public DateTime? LastActivityAt { get; init; }
    public string? ContainerId { get; init; }
    public string? ContainerName { get; init; }
    public string? OutputTail { get; init; }
    public DateTime? OutputTailCapturedAt { get; init; }
    public bool OutputTailTruncated { get; init; }
    public string? OutputTailSha256 { get; init; }
}

public sealed class PiSessionHostControlResult
{
    public required bool Succeeded { get; init; }
    public required string State { get; init; }
    public string? StateReason { get; init; }
    public DateTime? EndedAt { get; init; }
}

public interface IPiSessionHost
{
    string HostId { get; }
    Task<PiSessionHostLaunchResult> LaunchAsync(PiSessionLaunchPlan plan, CancellationToken cancellationToken = default);
    Task<PiSessionHostStatus> GetStatusAsync(PiSessionRecord session, CancellationToken cancellationToken = default);
    Task<PiSessionHostControlResult> TerminateAsync(PiSessionRecord session, CancellationToken cancellationToken = default);
    Task<PiSessionHostControlResult> CleanupAsync(PiSessionRecord session, PiDockerLaunchProfile? profile, CancellationToken cancellationToken = default);
}

public sealed class TmuxDockerPiSessionHost : IPiSessionHost
{
    private const string LabelPrefix = "@den.";
    private const int OutputTailLineCount = 80;
    private const int OutputTailMaxChars = 12000;
    private readonly PiDockerLaunchProfileOptions _options;
    private readonly IProcessRunner _runner;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(15);

    public TmuxDockerPiSessionHost(PiDockerLaunchProfileOptions options, IProcessRunner runner, Func<DateTime>? utcNow = null)
    {
        _options = options;
        _runner = runner;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string HostId => string.IsNullOrWhiteSpace(_options.HostId)
        ? Environment.MachineName
        : _options.HostId.Trim();

    public async Task<PiSessionHostLaunchResult> LaunchAsync(PiSessionLaunchPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(plan.LaunchProfile.PiStateDir);

        var newSessionArgs = new List<string> { "new-session", "-d", "-s", plan.Record.TmuxSessionName };
        if (!string.IsNullOrWhiteSpace(plan.LaunchProfile.DevDir))
        {
            newSessionArgs.Add("-c");
            newSessionArgs.Add(plan.LaunchProfile.DevDir);
        }
        foreach (var pair in plan.LaunchProfile.Environment)
        {
            newSessionArgs.Add("-e");
            newSessionArgs.Add($"{pair.Key}={pair.Value}");
        }

        var create = await RunTmuxAsync(newSessionArgs, cancellationToken).ConfigureAwait(false);
        if (!create.Succeeded)
        {
            return new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Failed,
                StateReason = TrimError(create.Stderr)
            };
        }

        await SetMetadataAsync(plan, cancellationToken).ConfigureAwait(false);

        var commandLine = RenderShellCommand(plan.LaunchCommand);
        var send = await RunTmuxAsync(["send-keys", "-t", plan.Record.TmuxSessionName, "-l", "--", commandLine], cancellationToken).ConfigureAwait(false);
        if (!send.Succeeded)
        {
            return new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Failed,
                StateReason = TrimError(send.Stderr)
            };
        }

        var enter = await RunTmuxAsync(["send-keys", "-t", plan.Record.TmuxSessionName, "Enter"], cancellationToken).ConfigureAwait(false);
        if (!enter.Succeeded)
        {
            return new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Failed,
                StateReason = TrimError(enter.Stderr)
            };
        }

        var now = _utcNow();
        return new PiSessionHostLaunchResult
        {
            State = PiSessionStates.Running,
            ContainerName = ExtractContainerName(plan.LaunchProfile),
            StartedAt = now,
            LastActivityAt = now,
        };
    }

    public async Task<PiSessionHostStatus> GetStatusAsync(PiSessionRecord session, CancellationToken cancellationToken = default)
    {
        var list = await RunTmuxAsync([
            "list-sessions",
            "-F",
            "#{session_name}\t#{session_created}\t#{session_activity}"
        ], cancellationToken).ConfigureAwait(false);

        if (!list.Succeeded)
        {
            return new PiSessionHostStatus
            {
                State = PiSessionStates.Stale,
                StateReason = "tmux session list failed: " + TrimError(list.Stderr),
            };
        }

        foreach (var line in list.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !string.Equals(parts[0], session.TmuxSessionName, StringComparison.Ordinal))
                continue;

            var output = await CaptureOutputTailAsync(session, cancellationToken).ConfigureAwait(false);
            return new PiSessionHostStatus
            {
                State = PiSessionStates.Running,
                LastActivityAt = FromUnixSeconds(parts[2]) ?? session.LastActivityAt,
                ContainerId = session.ContainerId,
                ContainerName = session.ContainerName,
                OutputTail = output?.Tail,
                OutputTailCapturedAt = output?.CapturedAt,
                OutputTailTruncated = output?.Truncated ?? false,
                OutputTailSha256 = output?.TailSha256,
            };
        }

        return new PiSessionHostStatus
        {
            State = PiSessionStates.Stale,
            StateReason = "tmux session was not found on the recorded host.",
        };
    }

    public async Task<PiSessionHostControlResult> TerminateAsync(PiSessionRecord session, CancellationToken cancellationToken = default)
    {
        var result = await RunTmuxAsync(["kill-session", "-t", session.TmuxSessionName], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && !TrimError(result.Stderr).Contains("can't find", StringComparison.OrdinalIgnoreCase))
        {
            return new PiSessionHostControlResult
            {
                Succeeded = false,
                State = PiSessionStates.Stale,
                StateReason = TrimError(result.Stderr),
            };
        }

        return new PiSessionHostControlResult
        {
            Succeeded = true,
            State = PiSessionStates.Completed,
            EndedAt = _utcNow(),
            StateReason = result.Succeeded ? "terminated by request" : "tmux session was already absent during termination",
        };
    }

    public async Task<PiSessionHostControlResult> CleanupAsync(PiSessionRecord session, PiDockerLaunchProfile? profile, CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            return new PiSessionHostControlResult
            {
                Succeeded = false,
                State = session.State,
                StateReason = "launch profile was unavailable; docker compose cleanup was not attempted.",
            };
        }

        var args = new List<string>
        {
            "compose",
            "--project-name", profile.ComposeProjectName,
            "-f", profile.ComposeFile,
            "down",
            "--remove-orphans"
        };
        var result = await _runner.RunAsync(_options.DockerExecutable, args, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new PiSessionHostControlResult
            {
                Succeeded = false,
                State = session.State,
                StateReason = TrimError(result.Stderr),
            };
        }

        return new PiSessionHostControlResult
        {
            Succeeded = true,
            State = session.State,
            StateReason = "docker compose cleanup completed",
        };
    }

    private async Task SetMetadataAsync(PiSessionLaunchPlan plan, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["project_id"] = plan.Record.ProjectId,
            ["task_id"] = plan.Record.TaskId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["workspace_id"] = plan.Record.WorkspaceId,
            ["run_id"] = plan.Record.RunId,
            ["session_id"] = plan.Record.SessionId,
            ["host_id"] = HostId,
            ["title"] = plan.Record.Title,
            ["profile_id"] = plan.LaunchProfile.ProfileId,
        };

        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;
            await RunTmuxAsync(["set-option", "-t", plan.Record.TmuxSessionName, LabelPrefix + pair.Key, pair.Value], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PiSessionOutputTail?> CaptureOutputTailAsync(PiSessionRecord session, CancellationToken cancellationToken)
    {
        var capture = await RunTmuxAsync([
            "capture-pane",
            "-p",
            "-t", session.TmuxSessionName,
            "-S", $"-{OutputTailLineCount}"
        ], cancellationToken).ConfigureAwait(false);
        if (!capture.Succeeded)
            return null;

        var normalized = capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n', '\r');
        var truncated = normalized.Length > OutputTailMaxChars || normalized.Split('\n').Length >= OutputTailLineCount;
        if (normalized.Length > OutputTailMaxChars)
            normalized = normalized[^OutputTailMaxChars..];

        return new PiSessionOutputTail(
            normalized,
            _utcNow(),
            truncated,
            ComputeSha256(normalized));
    }

    private Task<ProcessRunResult> RunTmuxAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        _runner.RunAsync(_options.TmuxExecutable, args, _commandTimeout, cancellationToken);

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? ExtractContainerName(PiDockerLaunchProfile profile)
    {
        for (var i = 0; i < profile.DockerComposeRunArgs.Count - 1; i++)
        {
            if (profile.DockerComposeRunArgs[i] == "--name")
                return profile.DockerComposeRunArgs[i + 1];
        }
        return $"{profile.ComposeProjectName}-{profile.Service}";
    }

    private static DateTime? FromUnixSeconds(string value) =>
        long.TryParse(value, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    private static string TrimError(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "unknown host error" : value.Trim();
        return trimmed.Length <= 2000 ? trimmed : trimmed[..2000];
    }

    /// <summary>
    /// Renders a typed argv vector as a shell line only for tmux's interactive pane.
    /// The recorded command remains the argv vector and all host process invocations
    /// use <see cref="IProcessRunner"/> argument lists.
    /// </summary>
    internal static string RenderShellCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new InvalidOperationException("Launch command must not be empty.");
        return string.Join(" ", args.Select(PosixQuote));
    }

    private static string PosixQuote(string value)
    {
        if (value.Length == 0)
            return "''";
        if (value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=' or ','))
            return value;
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

internal sealed record PiSessionOutputTail(string Tail, DateTime CapturedAt, bool Truncated, string TailSha256);

public static class PiSessionNaming
{
    public static string NewSessionId() => $"pi-{Guid.NewGuid():N}";

    public static string CreateTmuxSessionName(string projectId, string sessionId)
    {
        var slug = SafeSlug($"den-pi-{projectId}-{sessionId}");
        var suffix = ShortHash($"{projectId}:{sessionId}");
        var maxPrefixLength = 60 - suffix.Length - 1;
        if (slug.Length > maxPrefixLength)
            slug = slug[..maxPrefixLength].Trim('-', '.', '_');
        return $"{slug}-{suffix}";
    }

    private static string SafeSlug(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(c);
            else if (c is '-' or '_' or '.') builder.Append(c);
            else builder.Append('-');
        }
        var slug = builder.ToString().Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(slug) ? "den-pi-session" : slug;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}

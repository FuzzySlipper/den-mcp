using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

public sealed record LocalSessionSnapshot
{
    [JsonPropertyName("projectId")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("request")]
    public DesktopSessionSnapshotRequest Request { get; init; } = new();

    [JsonPropertyName("lastPublishStatus")]
    public string LastPublishStatus { get; init; } = "pending";

    [JsonPropertyName("lastPublishError")]
    public string? LastPublishError { get; init; }

    [JsonPropertyName("lastPublishedAt")]
    public string? LastPublishedAt { get; init; }

    [JsonPropertyName("artifactRoot")]
    public string? ArtifactRoot { get; init; }
}

public sealed record SessionScanResult
{
    [JsonPropertyName("snapshots")]
    public IReadOnlyList<LocalSessionSnapshot> Snapshots { get; init; } = [];

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; init; }
}

public sealed class PiSessionSnapshotBuilder
{
    public const int MaxRunDirs = 40;
    public const int MaxRecentActivity = 8;
    public const int MaxJsonlBytes = 512_000;

    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string> _nowString;

    public PiSessionSnapshotBuilder(
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string>? nowString = null)
    {
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _nowString = nowString ?? NowString;
    }

    public SessionScanResult ScanPiSessionSnapshots(
        OperatorSettings settings,
        IReadOnlyList<DenProject> projects)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(projects);

        var warnings = new List<string>();
        var root = ResolvePiRunRoot();
        if (root is null)
        {
            return new SessionScanResult
            {
                Snapshots = [],
                WarningCount = 1,
            };
        }

        if (!Directory.Exists(root))
        {
            return new SessionScanResult
            {
                Snapshots = [],
                WarningCount = 0,
            };
        }

        var candidates = RunCandidates(root, warnings)
            .OrderByDescending(candidate => candidate.ModifiedUtcTicks)
            .Take(MaxRunDirs)
            .ToList();

        var snapshots = new List<LocalSessionSnapshot>();
        foreach (var candidate in candidates)
        {
            try
            {
                var snapshot = SnapshotFromStatusPath(candidate.StatusPath, settings, projects);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"Unable to parse Pi run status {candidate.StatusPath}: {ex.Message}");
            }
        }

        return new SessionScanResult
        {
            Snapshots = snapshots,
            WarningCount = warnings.Count,
        };
    }

    public string? ResolvePiRunRoot()
    {
        var explicitRoot = TrimToOption(_getEnvironmentVariable("PI_SUBAGENT_RUNS_DIR"));
        if (explicitRoot is not null)
        {
            return explicitRoot;
        }

        var home = TrimToOption(_getEnvironmentVariable("HOME"));
        return home is null ? null : Path.Combine(home, ".pi", "agent", "den-subagent-runs");
    }

    internal LocalSessionSnapshot? SnapshotFromStatusPath(
        string statusPath,
        OperatorSettings settings,
        IReadOnlyList<DenProject> projects)
    {
        var statusText = File.ReadAllText(statusPath, Encoding.UTF8);
        var status = JsonSerializer.Deserialize<RunStatus>(statusText) ?? new RunStatus();
        var projectId = ProjectIdForStatus(status, projects);
        if (projectId is null)
        {
            return null;
        }

        var runDirectory = Path.GetDirectoryName(statusPath);
        var runId = TrimToOption(status.RunId)
            ?? TrimToOption(runDirectory is null ? null : Path.GetFileName(runDirectory))
            ?? "unknown-run";
        var sessionId = TrimToOption(status.PiSessionId) ?? $"pi-run-{runId}";
        var sessionFile = TrimToOption(status.PiSessionFilePath)
            ?? TrimToOption(status.Artifacts?.SessionFilePath);

        var activity = sessionFile is null
            ? RecentActivityResult.MissingSessionFilePath()
            : ReadRecentActivity(sessionFile);

        var phase = TrimToOption(status.CurrentPhase)
            ?? TrimToOption(status.State)
            ?? (TrimToOption(status.EndedAt) is null ? null : "complete")
            ?? "observed";
        var command = TrimToOption(status.CurrentCommand)
            ?? TrimToOption(activity.LatestTool)
            ?? TrimToOption(status.Backend);
        var artifactRoot = TrimToOption(status.Artifacts?.Dir) ?? runDirectory;

        // Derive normalized status from legacy phase/state (task #1009).
        // Mapping: "complete" -> "exited", "observed" -> "running" or "idle"
        // depending on activity, others map to "running".
        var normalizedStatus = phase switch
        {
            "complete" => "exited",
            "running" => "running",
            "working" or "coding" or "tool_use" => "running",
            "failed" => "failed",
            _ when TrimToOption(status.EndedAt) is not null => "exited",
            _ => "running"
        };

        var requestWarnings = activity.Warnings.ToList();
        if (TrimToOption(status.EndedAt) is not null)
        {
            requestWarnings.Add("Session is complete; snapshot is retained for correlation/history.");
        }

        var nowString = _nowString();
        var recentActivity = ToJsonElement(new
        {
            schema = "den_desktop_recent_activity",
            schema_version = 1,
            items = activity.Items,
            run = new
            {
                run_id = runId,
                pid = status.Pid,
                started_at = status.StartedAt,
                ended_at = status.EndedAt,
                exit_code = status.ExitCode,
            },
        });
        var childSessions = ToJsonElement(new
        {
            schema = "den_desktop_session_children",
            schema_version = 1,
            items = Array.Empty<object>(),
            note = "Sub-agent children are available from Den run records; local artifact scan does not infer a process tree yet.",
        });

        // Legacy Pi builder capability blob (preserved for backward compat).
        // All control flags are false for observer-only Pi artifact sessions.
        var controlCapabilities = ToJsonElement(new
        {
            schema = "den_desktop_session_capabilities",
            schema_version = 1,
            can_focus = false,
            can_stream_raw_terminal = false,
            can_send_input = false,
            can_stop = false,
            can_launch_managed_session = false,
            reason = "Artifact-observer mode only; no PTY ownership or safe controls are active in this spike.",
        });

        // Structured capabilities with OperatorSession vocabulary (task #1009).
        // Mapping from legacy Pi builder capability keys:
        //   can_stream_raw_terminal -> can_stream_terminal
        //   can_stop               -> can_terminate
        //   can_send_input         -> can_send_input
        //   can_launch_managed_session -> can_deliver_compiled_response
        //   can_focus              -> can_focus
        var capabilities = ToJsonElement(new
        {
            schema = "den_desktop_session_capabilities_v2",
            schema_version = 1,
            can_attach = false,
            can_detach = false,
            can_send_input = false,
            can_resize = false,
            can_terminate = false,
            can_kill = false,
            can_reconnect = false,
            can_focus = false,
            can_read_activity = activity.Items.Count > 0,
            can_stream_terminal = false,
            can_deliver_compiled_response = false,
            reason = "Artifact-observer mode only; no PTY ownership or safe controls are active.",
        });

        return new LocalSessionSnapshot
        {
            ProjectId = projectId,
            ArtifactRoot = artifactRoot,
            Request = new DesktopSessionSnapshotRequest
            {
                TaskId = status.TaskId,
                WorkspaceId = TrimToOption(status.WorkspaceId),
                SessionId = sessionId,
                ParentSessionId = null,
                AgentIdentity = "pi",
                Role = TrimToOption(status.Role),
                CurrentCommand = command,
                CurrentPhase = phase,
                // First-class OperatorSession fields (task #1009)
                Title = TrimToOption(runId), // use resolved runId (with fallback to directory name)
                DisplayName = TrimToOption(status.Role),
                Cwd = TrimToOption(status.Cwd),
                Kind = "artifact_observer",
                Backend = "pi_artifact",
                Status = normalizedStatus,
                StartedAt = status.StartedAt,
                LastActivityAt = activity.Items.Count > 0 ? nowString : null,
                ExitedAt = status.EndedAt,
                ExitCode = status.ExitCode,
                SourceDisplayName = settings.SourceDisplayName,
                Capabilities = capabilities,
                // Legacy fields preserved for backward compatibility
                RecentActivity = recentActivity,
                ChildSessions = childSessions,
                ControlCapabilities = controlCapabilities,
                Warnings = requestWarnings,
                SourceInstanceId = settings.SourceInstanceId,
                ObservedAt = nowString,
            },
            LastPublishStatus = "pending",
        };
    }

    internal static string? ProjectIdForStatus(RunStatus status, IReadOnlyList<DenProject> projects)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(projects);

        var cwd = TrimToOption(status.Cwd);
        if (cwd is not null)
        {
            var match = projects
                .Select(project => new { Project = project, Root = TrimToOption(project.RootPath) })
                .Where(item => item.Root is not null && IsPathPrefix(cwd, item.Root))
                .OrderByDescending(item => NormalizePathForPrefix(item.Root!).Length)
                .FirstOrDefault();
            if (match is not null)
            {
                return match.Project.Id;
            }

            var shortPathMatch = ProjectIdFromRepoShortPath(cwd, projects);
            if (shortPathMatch is not null)
            {
                return shortPathMatch;
            }
        }

        return projects.Count == 1 ? projects[0].Id : null;
    }

    private static string? ProjectIdFromRepoShortPath(string cwd, IReadOnlyList<DenProject> projects)
    {
        var repoName = RepositoryRootName(cwd);
        if (repoName is not null)
        {
            var match = MatchProjectShortName(repoName, projects);
            if (match is not null)
            {
                return match;
            }
        }

        foreach (var segment in PathSegments(cwd).Reverse())
        {
            var match = MatchProjectShortName(segment, projects);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string? MatchProjectShortName(string shortName, IReadOnlyList<DenProject> projects)
    {
        var idMatch = projects.FirstOrDefault(project =>
            string.Equals(project.Id, shortName, StringComparison.Ordinal));
        if (idMatch is not null)
        {
            return idMatch.Id;
        }

        var rootNameMatch = projects.FirstOrDefault(project =>
        {
            var rootPath = TrimToOption(project.RootPath);
            var rootShortName = rootPath is null ? null : ShortNameFromPath(rootPath);
            return rootShortName is not null && string.Equals(rootShortName, shortName, PathComparison);
        });
        return rootNameMatch?.Id;
    }

    private static string? ShortNameFromPath(string path)
    {
        try
        {
            return Path.GetFileName(NormalizePathForPrefix(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? RepositoryRootName(string cwd)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(cwd));
            while (current is not null)
            {
                var gitPath = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    return current.Name;
                }

                current = current.Parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<string> PathSegments(string path)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        return path.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static (JsonElement Item, string? LatestTool)? ActivityFromJsonlLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var timestamp = root.TryGetProperty("timestamp", out var timestampElement) && timestampElement.ValueKind == JsonValueKind.String
            ? timestampElement.GetString()
            : null;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var role = message.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
            ? roleElement.GetString() ?? "event"
            : "event";
        if (role == "toolResult")
        {
            var toolName = message.TryGetProperty("toolName", out var toolNameElement) && toolNameElement.ValueKind == JsonValueKind.String
                ? toolNameElement.GetString() ?? "tool result"
                : "tool result";
            var summary = SummarizeToolResult(message);
            return (ToJsonElement(new Dictionary<string, object?>
            {
                ["kind"] = "tool_result",
                ["role"] = role,
                ["tool"] = toolName,
                ["summary"] = summary,
                ["timestamp"] = timestamp,
            }), null);
        }

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var fragments = new List<string>();
        string? latestTool = null;
        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (typeElement.GetString())
            {
                case "text":
                    if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                    {
                        fragments.Add(textElement.GetString() ?? string.Empty);
                    }
                    break;
                case "thinking":
                    if (item.TryGetProperty("thinking", out var thinkingElement) && thinkingElement.ValueKind == JsonValueKind.String)
                    {
                        fragments.Add("thinking: " + (thinkingElement.GetString() ?? string.Empty));
                    }
                    break;
                case "toolCall":
                    if (item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                    {
                        latestTool = nameElement.GetString();
                        fragments.Add("tool: " + latestTool);
                    }
                    break;
            }
        }

        if (fragments.Count == 0)
        {
            return null;
        }

        return (ToJsonElement(new Dictionary<string, object?>
        {
            ["kind"] = latestTool is null ? "message" : "assistant_tool_call",
            ["role"] = role,
            ["summary"] = TruncateSummary(string.Join(" | ", fragments)),
            ["timestamp"] = timestamp,
        }), latestTool);
    }

    internal static string TruncateSummary(string text)
    {
        var normalized = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= 180)
        {
            return normalized;
        }

        return new string(normalized.Take(180).ToArray()) + "…";
    }

    private static IReadOnlyList<RunCandidate> RunCandidates(string root, List<string> warnings)
    {
        try
        {
            var candidates = new List<RunCandidate>();
            foreach (var runDirectory in Directory.EnumerateDirectories(root))
            {
                var statusPath = Path.Combine(runDirectory, "status.json");
                if (!File.Exists(statusPath))
                {
                    continue;
                }

                long modifiedUtcTicks;
                try
                {
                    modifiedUtcTicks = Directory.GetLastWriteTimeUtc(runDirectory).Ticks;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Unable to read Pi run artifact metadata at {runDirectory}: {ex.Message}");
                    modifiedUtcTicks = 0;
                }

                candidates.Add(new RunCandidate(statusPath, modifiedUtcTicks));
            }

            return candidates;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Unable to scan Pi run artifacts at {root}: {ex.Message}");
            return [];
        }
    }

    private static RecentActivityResult ReadRecentActivity(string path)
    {
        var warnings = new List<string>();
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return new RecentActivityResult([], null, [$"Unable to read Pi session file {path}: file does not exist."]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RecentActivityResult([], null, [$"Unable to read Pi session file {path}: {ex.Message}"]);
        }

        var truncated = info.Length > MaxJsonlBytes;
        if (truncated)
        {
            warnings.Add($"Pi session file {path} is large/truncated; reading last bounded {MaxJsonlBytes} bytes for the artifact observer.");
        }

        string text;
        try
        {
            text = ReadBoundedUtf8Text(path, info.Length, truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new RecentActivityResult([], null, [$"Unable to read Pi session file {path}: {ex.Message}"]);
        }

        var lines = text.Split('\n');
        var start = Math.Max(0, lines.Length - 200);
        var items = new List<JsonElement>();
        string? latestTool = null;
        for (var index = start; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            try
            {
                var parsed = ActivityFromJsonlLine(line);
                if (parsed is null)
                {
                    continue;
                }

                if (parsed.Value.LatestTool is not null)
                {
                    latestTool = parsed.Value.LatestTool;
                }

                items.Add(parsed.Value.Item);
            }
            catch (JsonException)
            {
                // Ignore incomplete or non-message JSONL entries; the session file is an append-only log.
            }
        }

        if (items.Count > MaxRecentActivity)
        {
            items = items.Skip(items.Count - MaxRecentActivity).ToList();
        }

        return new RecentActivityResult(items, latestTool, warnings);
    }

    private static string ReadBoundedUtf8Text(string path, long length, bool truncated)
    {
        if (!truncated)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        // Deliberately differs from the Rust spike: large append-only Pi session JSONL
        // files are tailed to a bounded byte window so the observer cannot load an
        // unbounded artifact into memory while the desktop polls for recent activity.
        var bytesToRead = (int)Math.Min(MaxJsonlBytes, length);
        var buffer = new byte[bytesToRead];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(length - bytesToRead, SeekOrigin.Begin);
        var offset = 0;
        while (offset < bytesToRead)
        {
            var read = stream.Read(buffer, offset, bytesToRead - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, offset);
        var firstNewline = text.IndexOf('\n', StringComparison.Ordinal);
        return firstNewline < 0 ? text : text[(firstNewline + 1)..];
    }

    private static string SummarizeToolResult(JsonElement message)
    {
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    return TruncateSummary(textElement.GetString() ?? string.Empty);
                }
            }
        }

        return "tool result";
    }

    private static bool IsPathPrefix(string cwd, string? root)
    {
        if (root is null)
        {
            return false;
        }

        // Use path-aware normalization instead of the Rust spike's simple string
        // prefix check so sibling paths and platform-specific separators/casing are
        // handled safely when correlating sessions to project roots.
        try
        {
            var normalizedCwd = NormalizePathForPrefix(cwd);
            var normalizedRoot = NormalizePathForPrefix(root);
            if (normalizedCwd.Equals(normalizedRoot, PathComparison))
            {
                return true;
            }

            return normalizedCwd.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison)
                || normalizedCwd.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string NormalizePathForPrefix(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (root is not null && fullPath.Length <= root.Length)
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? TrimToOption(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value);
    }

    private static string NowString()
    {
        return DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }

    private sealed record RunCandidate(string StatusPath, long ModifiedUtcTicks);

    internal sealed record RunStatus
    {
        [JsonPropertyName("run_id")]
        public string? RunId { get; init; }

        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("task_id")]
        public long? TaskId { get; init; }

        [JsonPropertyName("cwd")]
        public string? Cwd { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("backend")]
        public string? Backend { get; init; }

        [JsonPropertyName("pid")]
        public long? Pid { get; init; }

        [JsonPropertyName("started_at")]
        public string? StartedAt { get; init; }

        [JsonPropertyName("ended_at")]
        public string? EndedAt { get; init; }

        [JsonPropertyName("exit_code")]
        public long? ExitCode { get; init; }

        [JsonPropertyName("current_command")]
        public string? CurrentCommand { get; init; }

        [JsonPropertyName("current_phase")]
        public string? CurrentPhase { get; init; }

        [JsonPropertyName("pi_session_id")]
        public string? PiSessionId { get; init; }

        [JsonPropertyName("pi_session_file_path")]
        public string? PiSessionFilePath { get; init; }

        [JsonPropertyName("workspace_id")]
        public string? WorkspaceId { get; init; }

        [JsonPropertyName("artifacts")]
        public RunArtifacts? Artifacts { get; init; }
    }

    internal sealed record RunArtifacts
    {
        [JsonPropertyName("dir")]
        public string? Dir { get; init; }

        [JsonPropertyName("session_file_path")]
        public string? SessionFilePath { get; init; }
    }

    private sealed record RecentActivityResult(
        IReadOnlyList<JsonElement> Items,
        string? LatestTool,
        IReadOnlyList<string> Warnings)
    {
        public static RecentActivityResult MissingSessionFilePath()
        {
            return new RecentActivityResult(
                [],
                null,
                ["Pi session file path was not recorded for this run."]);
        }
    }
}

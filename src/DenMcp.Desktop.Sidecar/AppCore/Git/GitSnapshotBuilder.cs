using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

public sealed record GitScope
{
    [JsonPropertyName("projectId")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("projectName")]
    public string? ProjectName { get; init; }

    [JsonPropertyName("taskId")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("rootPath")]
    public string RootPath { get; init; } = string.Empty;

    [JsonPropertyName("sourceKind")]
    public string SourceKind { get; init; } = string.Empty;

    internal string Key => string.Join(':', ProjectId, WorkspaceId ?? string.Empty, RootPath);
}

public sealed record LocalGitSnapshot
{
    [JsonPropertyName("scope")]
    public GitScope Scope { get; init; } = new();

    [JsonPropertyName("request")]
    public DesktopGitSnapshotRequest Request { get; init; } = new();

    [JsonPropertyName("lastPublishStatus")]
    public string LastPublishStatus { get; init; } = "pending";

    [JsonPropertyName("lastPublishError")]
    public string? LastPublishError { get; init; }

    [JsonPropertyName("lastPublishedAt")]
    public string? LastPublishedAt { get; init; }
}

public sealed record GitCommandResult
{
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public interface IGitCommandRunner
{
    Task<GitCommandResult> RunGitAsync(string rootPath, IReadOnlyList<string> args, CancellationToken cancellationToken = default);
}

public sealed class SystemGitCommandRunner : IGitCommandRunner
{
    public async Task<GitCommandResult> RunGitAsync(
        string rootPath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(args);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(rootPath);
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("git process did not start.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"Failed to start git: {ex.Message}", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new GitCommandResult
        {
            ExitCode = process.ExitCode,
            Stdout = stdout,
            Stderr = stderr,
        };
    }
}

public sealed class GitSnapshotBuilder
{
    public const int MaxDiffFilesPerScope = 20;
    public const int MaxDiffBytes = 64 * 1024;

    private static readonly string[] StatusArgs = ["status", "--porcelain=v2", "--branch", "--untracked-files=all"];

    private readonly IGitCommandRunner _runner;

    public GitSnapshotBuilder(IGitCommandRunner? runner = null)
    {
        _runner = runner ?? new SystemGitCommandRunner();
    }

    public static IReadOnlyList<GitScope> BuildGitScopes(
        IReadOnlyList<DenProject> projects,
        IReadOnlyList<DenAgentWorkspace> workspaces)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(workspaces);

        var scopes = new List<GitScope>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            var rootPath = TrimToOption(project.RootPath);
            if (rootPath is null)
            {
                continue;
            }

            var scope = new GitScope
            {
                ProjectId = project.Id,
                ProjectName = project.Name,
                RootPath = rootPath,
                SourceKind = "project_root",
            };
            if (seen.Add(scope.Key))
            {
                scopes.Add(scope);
            }
        }

        foreach (var workspace in workspaces)
        {
            if (workspace.State is "archived" or "complete" or "failed")
            {
                continue;
            }

            var rootPath = TrimToOption(workspace.WorktreePath);
            if (rootPath is null)
            {
                continue;
            }

            var scope = new GitScope
            {
                ProjectId = workspace.ProjectId,
                TaskId = workspace.TaskId,
                WorkspaceId = workspace.Id,
                RootPath = rootPath,
                SourceKind = "agent_workspace",
            };
            if (seen.Add(scope.Key))
            {
                scopes.Add(scope);
            }
        }

        return scopes;
    }

    public async Task<LocalGitSnapshot> InspectScopeAsync(
        GitScope scope,
        OperatorSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);

        var observedAt = NowString();
        DesktopGitSnapshotRequest request;
        if (!Directory.Exists(scope.RootPath))
        {
            request = BaseRequest(
                scope,
                settings,
                observedAt,
                DesktopSnapshotState.PathNotVisible,
                [$"Path is not visible on this machine: {scope.RootPath}"]);
        }
        else
        {
            try
            {
                var result = await _runner.RunGitAsync(scope.RootPath, StatusArgs, cancellationToken).ConfigureAwait(false);
                request = SnapshotFromGitStatus(scope, settings, observedAt, result, settings.MaxChangedFiles);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                request = BaseRequest(scope, settings, observedAt, DesktopSnapshotState.GitError, [ex.Message]);
            }
        }

        return new LocalGitSnapshot
        {
            Scope = scope,
            Request = request,
            LastPublishStatus = "pending",
        };
    }

    public async Task<IReadOnlyList<DesktopDiffSnapshotRequest>> InspectDiffSnapshotsAsync(
        LocalGitSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Request.State != DesktopSnapshotState.Ok)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var diffSnapshots = new List<DesktopDiffSnapshotRequest>();
        foreach (var file in snapshot.Request.ChangedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (diffSnapshots.Count >= MaxDiffFilesPerScope)
            {
                break;
            }

            if (!seen.Add(file.Path) || !IsSafeRelativeGitPath(file.Path))
            {
                continue;
            }

            var unstaged = await BuildDiffSnapshotAsync(snapshot, file.Path, staged: false, cancellationToken).ConfigureAwait(false);
            if (unstaged is not null)
            {
                diffSnapshots.Add(unstaged);
            }

            if (diffSnapshots.Count >= MaxDiffFilesPerScope)
            {
                break;
            }

            if (file.IndexStatus is not null && file.IndexStatus != "." && file.IndexStatus != "?")
            {
                var staged = await BuildDiffSnapshotAsync(snapshot, file.Path, staged: true, cancellationToken).ConfigureAwait(false);
                if (staged is not null)
                {
                    diffSnapshots.Add(staged);
                }
            }
        }

        return diffSnapshots;
    }

    public static DesktopGitSnapshotRequest SnapshotFromGitStatus(
        GitScope scope,
        OperatorSettings settings,
        string observedAt,
        GitCommandResult status,
        int maxChangedFiles)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(status);

        if (status.ExitCode != 0)
        {
            var state = status.Stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                ? DesktopSnapshotState.NotGitRepository
                : DesktopSnapshotState.GitError;
            var request = BaseRequest(
                scope,
                settings,
                observedAt,
                state,
                [FormatGitError("git status", status)]);
            return request with { Truncated = status.Truncated };
        }

        var parsed = ParsePorcelainV2(scope, settings, observedAt, status.Stdout, maxChangedFiles);
        var warnings = parsed.Warnings.Concat(status.Warnings).ToList();
        if (parsed.Upstream is null)
        {
            warnings.Add("No upstream branch reported by git status.");
        }

        if (parsed.IsDetached)
        {
            warnings.Add("Repository is in detached HEAD state.");
        }

        return parsed with
        {
            Truncated = status.Truncated || parsed.Truncated,
            Warnings = warnings,
        };
    }

    public static DesktopGitSnapshotRequest ParsePorcelainV2(
        GitScope scope,
        OperatorSettings settings,
        string observedAt,
        string output,
        int maxChangedFiles)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);

        var request = BaseRequest(scope, settings, observedAt, DesktopSnapshotState.Ok, []);
        var changedFiles = new List<GitFileStatus>();
        var truncated = false;
        var limit = Math.Max(0, maxChangedFiles);

        foreach (var rawLine in (output ?? string.Empty).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                request = ParseBranchHeader(request, line[2..]);
                continue;
            }

            if (changedFiles.Count >= limit)
            {
                truncated = true;
                continue;
            }

            var file = ParsePorcelainFile(line);
            if (file is not null)
            {
                changedFiles.Add(file);
            }
        }

        return request with
        {
            ChangedFiles = changedFiles,
            DirtyCounts = CountDirty(changedFiles),
            Truncated = truncated,
        };
    }

    public static GitFileStatus? ParsePorcelainFile(string line)
    {
        if (line.StartsWith("? ", StringComparison.Ordinal))
        {
            return new GitFileStatus
            {
                Path = line[2..],
                OldPath = null,
                IndexStatus = "?",
                WorktreeStatus = "?",
                Category = "untracked",
                IsUntracked = true,
            };
        }

        if (line.StartsWith("1 ", StringComparison.Ordinal))
        {
            var parts = line.Split(' ', 9, StringSplitOptions.None);
            if (parts.Length < 9)
            {
                return null;
            }

            var index = StatusChar(parts[1], 0, '.');
            var worktree = StatusChar(parts[1], 1, '.');
            return new GitFileStatus
            {
                Path = parts[8],
                IndexStatus = index,
                WorktreeStatus = worktree,
                Category = CategoryFromStatus(index, worktree, untracked: false),
                IsUntracked = false,
            };
        }

        if (line.StartsWith("2 ", StringComparison.Ordinal))
        {
            var parts = line.Split(' ', 10, StringSplitOptions.None);
            if (parts.Length < 10)
            {
                return null;
            }

            var index = StatusChar(parts[1], 0, 'R');
            var worktree = StatusChar(parts[1], 1, '.');
            var paths = SplitRenamePaths(parts[9]);
            return new GitFileStatus
            {
                Path = paths.NewPath,
                OldPath = paths.OldPath,
                IndexStatus = index,
                WorktreeStatus = worktree,
                Category = "renamed",
                IsUntracked = false,
            };
        }

        return null;
    }

    public static GitDirtyCounts CountDirty(IReadOnlyList<GitFileStatus> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        long staged = 0;
        long unstaged = 0;
        long untracked = 0;
        long modified = 0;
        long added = 0;
        long deleted = 0;
        long renamed = 0;

        foreach (var file in files)
        {
            if (file.IsUntracked)
            {
                untracked++;
            }

            if (IsChangedStatus(file.IndexStatus))
            {
                staged++;
            }

            if (IsChangedStatus(file.WorktreeStatus))
            {
                unstaged++;
            }

            switch (file.Category)
            {
                case "modified":
                    modified++;
                    break;
                case "added":
                    added++;
                    break;
                case "deleted":
                    deleted++;
                    break;
                case "renamed":
                    renamed++;
                    break;
            }
        }

        return new GitDirtyCounts
        {
            Total = files.Count,
            Staged = staged,
            Unstaged = unstaged,
            Untracked = untracked,
            Modified = modified,
            Added = added,
            Deleted = deleted,
            Renamed = renamed,
        };
    }

    /// <summary>
    /// Validates that <paramref name="path"/> is a safe relative path for git operations.
    /// </summary>
    /// <remarks>
    /// This check is intentionally more defensive than the Rust reference implementation.
    /// Extra guards beyond the original:
    /// <list type="bullet">
    ///   <item><description>Null-byte rejection — prevents injection through C-string boundaries.</description></item>
    ///   <item><description>Whitespace-only rejection — avoids empty or meaningless paths.</description></item>
    ///   <item><description>Windows drive-letter rejection (e.g., <c>C:\secret</c>) — cross-platform path safety.</description></item>
    ///   <item><description>Explicit backslash segment handling — treats <c>\</c> as a path separator on all platforms.</description></item>
    /// </list>
    /// </remarks>
    public static bool IsSafeRelativeGitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        if (Path.IsPathRooted(path) || path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("\\", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return false;
        }

        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<string> DiffArgs(string path, bool staged)
    {
        return staged ? ["diff", "--cached", "--", path] : ["diff", "HEAD", "--", path];
    }

    /// <summary>
    /// Truncates <paramref name="value"/> to fit within <paramref name="maxBytes"/> UTF-8 bytes,
    /// ensuring the result ends on a valid UTF-8 character boundary.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="Encoding.GetByteCount(string)"/> for a zero-allocation fast path when the
    /// input already fits. The <see cref="Encoding.GetBytes(string)"/> allocation is bounded and
    /// acceptable here because diff content is already capped at <see cref="MaxDiffBytes"/> (64 KiB).
    /// </remarks>
    public static (string Text, bool Truncated) BoundText(string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Maximum byte count must be non-negative.");
        }

        // Fast path: avoid allocating the byte array when the text already fits.
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return (value, false);
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var end = maxBytes;
        while (end > 0 && (bytes[end] & 0b1100_0000) == 0b1000_0000)
        {
            end--;
        }

        return (Encoding.UTF8.GetString(bytes, 0, end), true);
    }

    public static bool LooksLikeBinaryDiff(string diff)
    {
        return diff.Contains("Binary files ", StringComparison.Ordinal)
            || diff.Contains("GIT binary patch", StringComparison.Ordinal);
    }

    private async Task<DesktopDiffSnapshotRequest?> BuildDiffSnapshotAsync(
        LocalGitSnapshot snapshot,
        string path,
        bool staged,
        CancellationToken cancellationToken)
    {
        GitCommandResult result;
        try
        {
            result = await _runner.RunGitAsync(snapshot.Request.RootPath, DiffArgs(path, staged), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return DiffWarningSnapshot(snapshot, path, staged, ex.Message);
        }

        if (result.ExitCode != 0)
        {
            return DiffWarningSnapshot(snapshot, path, staged, FormatGitError(staged ? "git diff --cached" : "git diff HEAD", result));
        }

        if (result.Stdout.Length == 0)
        {
            return null;
        }

        var warnings = result.Warnings.ToList();
        var (diff, truncated) = BoundText(result.Stdout, MaxDiffBytes);
        if (truncated)
        {
            warnings.Add($"Diff output truncated to {MaxDiffBytes} bytes.");
        }

        var binary = LooksLikeBinaryDiff(result.Stdout);
        if (binary)
        {
            warnings.Add("Diff appears to describe binary content.");
        }

        return new DesktopDiffSnapshotRequest
        {
            TaskId = snapshot.Request.TaskId,
            WorkspaceId = snapshot.Request.WorkspaceId,
            RootPath = snapshot.Request.RootPath,
            Path = path,
            BaseRef = "HEAD",
            HeadRef = null,
            MaxBytes = MaxDiffBytes,
            Staged = staged,
            Diff = diff,
            Truncated = truncated,
            Binary = binary,
            Warnings = warnings,
            SourceInstanceId = snapshot.Request.SourceInstanceId,
            SourceDisplayName = snapshot.Request.SourceDisplayName,
            ObservedAt = NowString(),
        };
    }

    private static DesktopDiffSnapshotRequest DiffWarningSnapshot(
        LocalGitSnapshot snapshot,
        string path,
        bool staged,
        string warning)
    {
        return new DesktopDiffSnapshotRequest
        {
            TaskId = snapshot.Request.TaskId,
            WorkspaceId = snapshot.Request.WorkspaceId,
            RootPath = snapshot.Request.RootPath,
            Path = path,
            BaseRef = "HEAD",
            HeadRef = null,
            MaxBytes = MaxDiffBytes,
            Staged = staged,
            Diff = string.Empty,
            Truncated = false,
            Binary = false,
            Warnings = [warning],
            SourceInstanceId = snapshot.Request.SourceInstanceId,
            SourceDisplayName = snapshot.Request.SourceDisplayName,
            ObservedAt = NowString(),
        };
    }

    private static DesktopGitSnapshotRequest BaseRequest(
        GitScope scope,
        OperatorSettings settings,
        string observedAt,
        DesktopSnapshotState state,
        IReadOnlyList<string> warnings)
    {
        return new DesktopGitSnapshotRequest
        {
            TaskId = scope.TaskId,
            WorkspaceId = scope.WorkspaceId,
            RootPath = scope.RootPath,
            State = state,
            DirtyCounts = new GitDirtyCounts(),
            ChangedFiles = [],
            Warnings = warnings,
            SourceInstanceId = settings.SourceInstanceId,
            SourceDisplayName = settings.SourceDisplayName,
            ObservedAt = observedAt,
        };
    }

    private static DesktopGitSnapshotRequest ParseBranchHeader(DesktopGitSnapshotRequest request, string header)
    {
        if (header.StartsWith("branch.oid ", StringComparison.Ordinal))
        {
            var oid = header["branch.oid ".Length..].Trim();
            return request with { HeadSha = oid == "(initial)" ? null : oid };
        }

        if (header.StartsWith("branch.head ", StringComparison.Ordinal))
        {
            var head = header["branch.head ".Length..].Trim();
            var isDetached = head == "(detached)";
            return request with { IsDetached = isDetached, Branch = isDetached ? null : head };
        }

        if (header.StartsWith("branch.upstream ", StringComparison.Ordinal))
        {
            return request with { Upstream = TrimToOption(header["branch.upstream ".Length..]) };
        }

        if (header.StartsWith("branch.ab ", StringComparison.Ordinal))
        {
            long? ahead = request.Ahead;
            long? behind = request.Behind;
            foreach (var token in header["branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('+') && long.TryParse(token[1..], out var aheadValue))
                {
                    ahead = aheadValue;
                }
                else if (token.StartsWith('-') && long.TryParse(token[1..], out var behindValue))
                {
                    behind = behindValue;
                }
            }

            return request with { Ahead = ahead, Behind = behind };
        }

        return request;
    }

    private static (string NewPath, string? OldPath) SplitRenamePaths(string value)
    {
        var separator = value.IndexOf('\0');
        if (separator < 0)
        {
            separator = value.IndexOf('\t');
        }

        return separator < 0
            ? (value, null)
            : (value[..separator], value[(separator + 1)..]);
    }

    private static string StatusChar(string xy, int index, char fallback)
    {
        return xy.Length > index ? xy[index].ToString() : fallback.ToString();
    }

    private static bool IsChangedStatus(string? value)
    {
        return value is not null && value != "." && value != "?" && value != " ";
    }

    private static string CategoryFromStatus(string index, string worktree, bool untracked)
    {
        if (untracked || index == "?" || worktree == "?")
        {
            return "untracked";
        }

        if (index == "R" || worktree == "R")
        {
            return "renamed";
        }

        if (index == "D" || worktree == "D")
        {
            return "deleted";
        }

        if (index == "A" || worktree == "A")
        {
            return "added";
        }

        if (index == "M" || worktree == "M")
        {
            return "modified";
        }

        return "changed";
    }

    private static string FormatGitError(string command, GitCommandResult result)
    {
        var stderr = result.Stderr.Trim();
        return stderr.Length == 0
            ? $"{command} failed with exit code {result.ExitCode}"
            : $"{command} failed with exit code {result.ExitCode}: {stderr}";
    }

    private static string NowString()
    {
        return DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }

    private static string? TrimToOption(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

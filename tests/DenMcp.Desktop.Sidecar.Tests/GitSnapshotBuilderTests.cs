using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class GitSnapshotBuilderTests
{
    [Fact]
    public void BuildGitScopes_TrimsDeduplicatesAndFiltersInactiveWorkspaces()
    {
        var projects = new[]
        {
            new DenProject { Id = "den-mcp", Name = "Den MCP", RootPath = " /repo " },
            new DenProject { Id = "den-mcp", Name = "Duplicate", RootPath = "/repo" },
            new DenProject { Id = "blank", Name = "Blank", RootPath = "   " },
        };
        var workspaces = new[]
        {
            Workspace("active", "ws-active", "/repo-worktree"),
            Workspace("archived", "ws-archived", "/archived"),
            Workspace("complete", "ws-complete", "/complete"),
            Workspace("failed", "ws-failed", "/failed"),
            Workspace("active", "ws-blank", "   "),
            Workspace("active", "ws-active", "/repo-worktree"),
        };

        var scopes = GitSnapshotBuilder.BuildGitScopes(projects, workspaces);

        Assert.Equal(2, scopes.Count);
        var projectScope = Assert.Single(scopes, scope => scope.SourceKind == "project_root");
        Assert.Equal("/repo", projectScope.RootPath);
        Assert.Equal("Den MCP", projectScope.ProjectName);
        var workspaceScope = Assert.Single(scopes, scope => scope.SourceKind == "agent_workspace");
        Assert.Equal("ws-active", workspaceScope.WorkspaceId);
        Assert.Equal(998, workspaceScope.TaskId);
    }

    [Fact]
    public async Task InspectScope_ReportsMissingPathWithoutRunningGit()
    {
        var runner = new FakeGitRunner();
        var builder = new GitSnapshotBuilder(runner);
        var missingScope = Scope(rootPath: Path.Combine(Path.GetTempPath(), "den-missing-" + Guid.NewGuid().ToString("N")));

        var snapshot = await builder.InspectScopeAsync(missingScope, Settings());

        Assert.Equal(DesktopSnapshotState.PathNotVisible, snapshot.Request.State);
        Assert.Empty(runner.Calls);
        Assert.Equal(0, snapshot.Request.DirtyCounts.Total);
        Assert.Contains(snapshot.Request.Warnings, warning => warning.Contains("Path is not visible", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectScope_MapsVisibleNonGitAndGitErrorsToSnapshotStates()
    {
        var root = CreateTempDirectory();
        try
        {
            var nonGitRunner = new FakeGitRunner(new GitCommandResult
            {
                ExitCode = 128,
                Stderr = "fatal: not a git repository (or any of the parent directories): .git",
            });
            var nonGit = await new GitSnapshotBuilder(nonGitRunner).InspectScopeAsync(Scope(root), Settings());

            Assert.Equal(DesktopSnapshotState.NotGitRepository, nonGit.Request.State);
            Assert.Contains(nonGit.Request.Warnings, warning => warning.Contains("git status failed with exit code 128", StringComparison.Ordinal));
            Assert.Equal(new[] { "status", "--porcelain=v2", "--branch", "--untracked-files=all" }, nonGitRunner.Calls[0].Args);

            var gitErrorRunner = new FakeGitRunner(new GitCommandResult
            {
                ExitCode = 129,
                Stderr = "fatal: unsupported git invocation",
                Truncated = true,
            });
            var gitError = await new GitSnapshotBuilder(gitErrorRunner).InspectScopeAsync(Scope(root), Settings());

            Assert.Equal(DesktopSnapshotState.GitError, gitError.Request.State);
            Assert.True(gitError.Request.Truncated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InspectScope_ParsesPorcelainV2BranchFilesAndDirtyCounts()
    {
        var root = CreateTempDirectory();
        try
        {
            var output = "# branch.oid 1234567890abcdef\n" +
                "# branch.head task/998-dotnet-git-observer\n" +
                "# branch.upstream origin/task/998-dotnet-git-observer\n" +
                "# branch.ab +2 -1\n" +
                "1 M. N... 100644 100644 100644 aaa bbb src/modified file.cs\n" +
                "1 .D N... 100644 100644 000000 aaa bbb old.txt\n" +
                "1 A. N... 000000 100644 100644 aaa bbb added.txt\n" +
                "2 R. N... 100644 100644 100644 aaa bbb R100 new-name.txt\told-name.txt\n" +
                "? scratch.txt\n";
            var runner = new FakeGitRunner(new GitCommandResult { Stdout = output });

            var snapshot = await new GitSnapshotBuilder(runner).InspectScopeAsync(Scope(root), Settings());

            Assert.Equal(DesktopSnapshotState.Ok, snapshot.Request.State);
            Assert.Equal("task/998-dotnet-git-observer", snapshot.Request.Branch);
            Assert.Equal("1234567890abcdef", snapshot.Request.HeadSha);
            Assert.Equal("origin/task/998-dotnet-git-observer", snapshot.Request.Upstream);
            Assert.Equal(2, snapshot.Request.Ahead);
            Assert.Equal(1, snapshot.Request.Behind);
            Assert.Equal(5, snapshot.Request.DirtyCounts.Total);
            Assert.Equal(3, snapshot.Request.DirtyCounts.Staged);
            Assert.Equal(1, snapshot.Request.DirtyCounts.Unstaged);
            Assert.Equal(1, snapshot.Request.DirtyCounts.Untracked);
            Assert.Equal(1, snapshot.Request.DirtyCounts.Modified);
            Assert.Equal(1, snapshot.Request.DirtyCounts.Added);
            Assert.Equal(1, snapshot.Request.DirtyCounts.Deleted);
            Assert.Equal(1, snapshot.Request.DirtyCounts.Renamed);
            Assert.Equal("src/modified file.cs", snapshot.Request.ChangedFiles[0].Path);
            Assert.Equal("old-name.txt", snapshot.Request.ChangedFiles[3].OldPath);
            Assert.Empty(snapshot.Request.Warnings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotFromGitStatus_TruncatesChangedFilesAtConfiguredLimit()
    {
        var snapshot = GitSnapshotBuilder.SnapshotFromGitStatus(
            Scope(),
            Settings(),
            "2026-04-29T00:00:00.000Z",
            new GitCommandResult { Stdout = "? one.txt\n? two.txt\n? three.txt\n" },
            maxChangedFiles: 2);

        Assert.True(snapshot.Truncated);
        Assert.Equal(2, snapshot.ChangedFiles.Count);
        Assert.Equal(2, snapshot.DirtyCounts.Total);
        Assert.All(snapshot.ChangedFiles, file => Assert.Equal("untracked", file.Category));
    }

    [Fact]
    public void ParsePorcelainFile_ParsesRenameWithTabOrNulSeparator()
    {
        var tab = GitSnapshotBuilder.ParsePorcelainFile(
            "2 R. N... 100644 100644 100644 aaa bbb R100 new-name.txt\told-name.txt");
        var nul = GitSnapshotBuilder.ParsePorcelainFile(
            "2 R. N... 100644 100644 100644 aaa bbb R100 new-name.txt\0old-name.txt");

        Assert.NotNull(tab);
        Assert.Equal("new-name.txt", tab.Path);
        Assert.Equal("old-name.txt", tab.OldPath);
        Assert.NotNull(nul);
        Assert.Equal("new-name.txt", nul.Path);
        Assert.Equal("old-name.txt", nul.OldPath);
    }

    [Fact]
    public async Task InspectDiffSnapshots_SkipsUnsafePathsAndBuildsBoundedDiffRequests()
    {
        var largeBinaryDiff = "diff --git a/bin.dat b/bin.dat\nBinary files a/bin.dat and b/bin.dat differ\n" + new string('x', GitSnapshotBuilder.MaxDiffBytes + 32);
        var runner = new FakeGitRunner(
            new GitCommandResult { Stdout = "diff --git a/src/file.cs b/src/file.cs\n+unstaged\n" },
            new GitCommandResult { Stdout = "diff --git a/src/file.cs b/src/file.cs\n+staged\n" },
            new GitCommandResult { Stdout = largeBinaryDiff });
        var builder = new GitSnapshotBuilder(runner);
        var snapshot = LocalSnapshotWithFiles(
            File("../outside", "M", "M", "modified"),
            File("src/file.cs", "M", "M", "modified"),
            File("src/file.cs", "M", "M", "modified"),
            File("bin.dat", ".", "M", "modified"));

        var diffs = await builder.InspectDiffSnapshotsAsync(snapshot);

        Assert.Equal(3, diffs.Count);
        Assert.Equal(new[] { "diff", "HEAD", "--", "src/file.cs" }, runner.Calls[0].Args);
        Assert.Equal(new[] { "diff", "--cached", "--", "src/file.cs" }, runner.Calls[1].Args);
        Assert.Equal(new[] { "diff", "HEAD", "--", "bin.dat" }, runner.Calls[2].Args);
        Assert.DoesNotContain(runner.Calls, call => call.Args.Contains("../outside"));
        Assert.False(diffs[0].Staged);
        Assert.True(diffs[1].Staged);
        Assert.True(diffs[2].Binary);
        Assert.True(diffs[2].Truncated);
        Assert.Equal(GitSnapshotBuilder.MaxDiffBytes, System.Text.Encoding.UTF8.GetByteCount(diffs[2].Diff));
        Assert.Contains(diffs[2].Warnings, warning => warning.Contains("binary", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diffs[2].Warnings, warning => warning.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InspectDiffSnapshots_CapsDiffFilesPerScope()
    {
        var runner = new FakeGitRunner(Enumerable.Range(0, GitSnapshotBuilder.MaxDiffFilesPerScope + 5)
            .Select(index => new GitCommandResult { Stdout = $"diff --git a/file-{index}.txt b/file-{index}.txt\n" })
            .ToArray());
        var files = Enumerable.Range(0, GitSnapshotBuilder.MaxDiffFilesPerScope + 5)
            .Select(index => File($"file-{index}.txt", ".", "M", "modified"))
            .ToArray();
        var snapshot = LocalSnapshotWithFiles(files);

        var diffs = await new GitSnapshotBuilder(runner).InspectDiffSnapshotsAsync(snapshot);

        Assert.Equal(GitSnapshotBuilder.MaxDiffFilesPerScope, diffs.Count);
        Assert.Equal(GitSnapshotBuilder.MaxDiffFilesPerScope, runner.Calls.Count);
    }

    [Fact]
    public void DiffHelpers_ValidatePathsArgumentsBinaryAndUtf8Truncation()
    {
        Assert.True(GitSnapshotBuilder.IsSafeRelativeGitPath("src/main.cs"));
        Assert.True(GitSnapshotBuilder.IsSafeRelativeGitPath("./src/main.cs"));
        Assert.False(GitSnapshotBuilder.IsSafeRelativeGitPath(""));
        Assert.False(GitSnapshotBuilder.IsSafeRelativeGitPath("../secret"));
        Assert.False(GitSnapshotBuilder.IsSafeRelativeGitPath("/tmp/secret"));
        Assert.False(GitSnapshotBuilder.IsSafeRelativeGitPath("C:\\secret"));

        Assert.Equal(new[] { "diff", "HEAD", "--", "src/main.cs" }, GitSnapshotBuilder.DiffArgs("src/main.cs", staged: false));
        Assert.Equal(new[] { "diff", "--cached", "--", "src/main.cs" }, GitSnapshotBuilder.DiffArgs("src/main.cs", staged: true));

        var (bounded, truncated) = GitSnapshotBuilder.BoundText("αβγδε", 5);
        Assert.Equal("αβ", bounded);
        Assert.True(truncated);
        Assert.True(GitSnapshotBuilder.LooksLikeBinaryDiff("diff --git a/bin b/bin\nBinary files a/bin and b/bin differ"));
        Assert.True(GitSnapshotBuilder.LooksLikeBinaryDiff("GIT binary patch\nliteral 0"));
    }

    [Fact]
    public async Task FakeGitRunner_ReturnsGitErrorWhenQueueIsEmpty()
    {
        var runner = new FakeGitRunner();

        var result = await runner.RunGitAsync("/repo", ["status"], CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No fake git result queued", result.Stderr, StringComparison.Ordinal);
    }

    private static OperatorSettings Settings()
    {
        return new OperatorSettings
        {
            DenBaseUrl = "http://localhost:5199",
            SourceInstanceId = "desktop-test",
            SourceDisplayName = "Desktop Test",
            PollIntervalSeconds = 30,
            MaxChangedFiles = 200,
        };
    }

    private static GitScope Scope(string? rootPath = null)
    {
        return new GitScope
        {
            ProjectId = "den-mcp",
            ProjectName = "Den MCP",
            TaskId = 998,
            WorkspaceId = "ws-998",
            RootPath = rootPath ?? "/repo",
            SourceKind = "agent_workspace",
        };
    }

    private static DenAgentWorkspace Workspace(string state, string id, string path)
    {
        return new DenAgentWorkspace
        {
            Id = id,
            ProjectId = "den-mcp",
            TaskId = 998,
            Branch = "task/998-dotnet-git-observer",
            WorktreePath = path,
            BaseBranch = "main",
            State = state,
        };
    }

    private static LocalGitSnapshot LocalSnapshotWithFiles(params GitFileStatus[] files)
    {
        return new LocalGitSnapshot
        {
            Scope = Scope(),
            Request = new DesktopGitSnapshotRequest
            {
                TaskId = 998,
                WorkspaceId = "ws-998",
                RootPath = "/repo",
                State = DesktopSnapshotState.Ok,
                DirtyCounts = GitSnapshotBuilder.CountDirty(files),
                ChangedFiles = files,
                SourceInstanceId = "desktop-test",
                SourceDisplayName = "Desktop Test",
                ObservedAt = "2026-04-29T00:00:00.000Z",
            },
        };
    }

    private static GitFileStatus File(string path, string index, string worktree, string category)
    {
        return new GitFileStatus
        {
            Path = path,
            IndexStatus = index,
            WorktreeStatus = worktree,
            Category = category,
            IsUntracked = index == "?" || worktree == "?",
        };
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "den-git-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeGitRunner : IGitCommandRunner
    {
        private readonly Queue<GitCommandResult> _results;

        public FakeGitRunner(params GitCommandResult[] results)
        {
            _results = new Queue<GitCommandResult>(results);
        }

        public List<GitCall> Calls { get; } = [];

        public Task<GitCommandResult> RunGitAsync(string rootPath, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new GitCall(rootPath, args.ToArray()));
            if (_results.Count == 0)
            {
                return Task.FromResult(new GitCommandResult
                {
                    ExitCode = 1,
                    Stderr = "No fake git result queued.",
                });
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed record GitCall(string RootPath, IReadOnlyList<string> Args);
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Server.Tests;

public sealed class GitInspectionApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly string _projectId = $"git-api-test-{Guid.NewGuid():N}";
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"den-git-api-repo-{Guid.NewGuid():N}");
    private GitAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        CreateGitRepo(_repoRoot);
        _factory = new GitAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project
        {
            Id = _projectId,
            Name = "Git API Test",
            RootPath = _repoRoot
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Status_ReturnsStructuredBranchAndDirtyFileState()
    {
        await File.AppendAllTextAsync(Path.Combine(_repoRoot, "tracked.txt"), "modified\n");
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, "scratch.md"), "scratch\n");

        var response = await _client.GetAsync($"/api/projects/{_projectId}/git/status");
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<GitStatusResponse>(JsonOpts);

        Assert.NotNull(status);
        Assert.True(status!.IsGitRepository);
        Assert.NotNull(status.HeadSha);
        Assert.True(status.DirtyCounts.Total >= 2);
        Assert.Contains(status.Files, file => file.Path == "tracked.txt" && file.Category == "modified");
        Assert.Contains(status.Files, file => file.Path == "scratch.md" && file.IsUntracked);
    }

    [Fact]
    public async Task Diff_ReturnsBoundedDiffAndRejectsEscapingPaths()
    {
        await File.AppendAllTextAsync(Path.Combine(_repoRoot, "tracked.txt"), string.Join('\n', Enumerable.Range(0, 500).Select(i => $"line {i}")));

        var diffResponse = await _client.GetAsync($"/api/projects/{_projectId}/git/diff?path=tracked.txt&maxBytes=1024");
        diffResponse.EnsureSuccessStatusCode();
        var diff = await diffResponse.Content.ReadFromJsonAsync<GitDiffResponse>(JsonOpts);
        Assert.NotNull(diff);
        Assert.True(diff!.Diff.Contains("tracked.txt", StringComparison.Ordinal));
        Assert.True(diff.MaxBytes == 1024);

        var badResponse = await _client.GetAsync($"/api/projects/{_projectId}/git/diff?path=../outside.txt");
        badResponse.EnsureSuccessStatusCode();
        var bad = await badResponse.Content.ReadFromJsonAsync<GitDiffResponse>(JsonOpts);
        Assert.NotNull(bad);
        Assert.Contains(bad!.Errors, error => error.Contains("escapes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Status_ReturnsNotFoundForMissingProject()
    {
        var response = await _client.GetAsync("/api/projects/missing-project/git/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void CreateGitRepo(string root)
    {
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "initial\n");
        RunGit(root, "add", "tracked.txt");
        RunGit(root, "commit", "-m", "initial");
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.WaitForExit(10_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
    }

    private sealed class GitAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-git-api-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["db-path"] = _dbPath,
                    ["llm-endpoint"] = "http://localhost/fake",
                    ["llm-api-key"] = "test-key",
                    ["llm-model"] = "fake"
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}

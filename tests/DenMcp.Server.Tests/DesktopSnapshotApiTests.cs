using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Server.Tests;

public class DesktopSnapshotApiTests : IAsyncLifetime
{
    private const string ProjectId = "desktop-snapshot-api-test";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private SnapshotAppFactory _factory = null!;
    private HttpClient _client = null!;
    private ProjectTask _task = null!;
    private AgentWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {
        _factory = new SnapshotAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Desktop Snapshot API Test" });

        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        _task = await tasks.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Desktop snapshot task" });

        var workspaces = scope.ServiceProvider.GetRequiredService<IAgentWorkspaceRepository>();
        _workspace = await workspaces.UpsertAsync(new AgentWorkspace
        {
            Id = "ws-desktop-api",
            ProjectId = ProjectId,
            TaskId = _task.Id,
            Branch = "task/desktop-api",
            WorktreePath = "/tmp/desktop-api",
            BaseBranch = "main"
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DesktopGitSnapshots_CreateListAndLatestWithStaleState()
    {
        var response = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/desktop/git-snapshots", new
        {
            task_id = _task.Id,
            workspace_id = _workspace.Id,
            root_path = _workspace.WorktreePath,
            state = "ok",
            branch = "task/desktop-api",
            head_sha = "abcdef123456",
            upstream = "origin/task/desktop-api",
            ahead = 1,
            behind = 0,
            dirty_counts = new { total = 1, modified = 1 },
            changed_files = new[] { new { path = "src/Foo.cs", worktree_status = "M", category = "modified" } },
            warnings = new[] { "published by desktop" },
            source_instance_id = "desktop-a",
            source_display_name = "Desktop A",
            observed_at = DateTime.UtcNow.AddMinutes(-5)
        }, JsonOpts);
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<DesktopGitSnapshot>(JsonOpts);
        Assert.NotNull(created);
        Assert.Equal("desktop-a", created!.SourceInstanceId);
        Assert.Equal("src/Foo.cs", Assert.Single(created.ChangedFiles).Path);

        var listResponse = await _client.GetAsync($"/api/projects/{ProjectId}/desktop/git-snapshots?workspaceId={_workspace.Id}&sourceInstanceId=desktop-a&staleAfterSeconds=600");
        listResponse.EnsureSuccessStatusCode();
        var listed = await listResponse.Content.ReadFromJsonAsync<List<DesktopGitSnapshot>>(JsonOpts);
        var only = Assert.Single(listed!);
        Assert.False(only.IsStale);

        var staleLatestResponse = await _client.GetAsync($"/api/projects/{ProjectId}/desktop/git-snapshots/latest?workspaceId={_workspace.Id}&sourceInstanceId=desktop-a&staleAfterSeconds=1");
        staleLatestResponse.EnsureSuccessStatusCode();
        using var latestJson = JsonDocument.Parse(await staleLatestResponse.Content.ReadAsStringAsync());
        Assert.Equal("source_offline", latestJson.RootElement.GetProperty("state").GetString());
        Assert.True(latestJson.RootElement.GetProperty("is_stale").GetBoolean());
        Assert.Equal("stale", latestJson.RootElement.GetProperty("freshness_status").GetString());
    }

    [Fact]
    public async Task DesktopDiffAndSessionSnapshots_CreateAndReadRouteShapes()
    {
        var diffResponse = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/desktop/diff-snapshots", new
        {
            task_id = _task.Id,
            workspace_id = _workspace.Id,
            root_path = _workspace.WorktreePath,
            path = "src/Foo.cs",
            base_ref = "main",
            head_ref = "task/desktop-api",
            max_bytes = 4096,
            diff = "diff --git a/src/Foo.cs b/src/Foo.cs",
            source_instance_id = "desktop-a",
            observed_at = DateTime.UtcNow
        }, JsonOpts);
        diffResponse.EnsureSuccessStatusCode();

        var diffLatest = await _client.GetAsync($"/api/projects/{ProjectId}/desktop/diff-snapshots/latest?taskId={_task.Id}&workspaceId={_workspace.Id}&sourceInstanceId=desktop-a&rootPath={Uri.EscapeDataString(_workspace.WorktreePath)}&path=src%2FFoo.cs&baseRef=main&headRef=task%2Fdesktop-api");
        diffLatest.EnsureSuccessStatusCode();
        using var diffJson = JsonDocument.Parse(await diffLatest.Content.ReadAsStringAsync());
        Assert.Equal("fresh", diffJson.RootElement.GetProperty("state").GetString());
        Assert.Contains("diff --git", diffJson.RootElement.GetProperty("snapshot").GetProperty("diff").GetString());

        var sessionResponse = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/desktop/session-snapshots", new
        {
            task_id = _task.Id,
            workspace_id = _workspace.Id,
            session_id = "pty-1",
            agent_identity = "pi",
            role = "conductor",
            current_command = "pi",
            current_phase = "working",
            control_capabilities = new { focus = true, terminate = false },
            source_instance_id = "desktop-a",
            observed_at = DateTime.UtcNow
        }, JsonOpts);
        sessionResponse.EnsureSuccessStatusCode();

        var sessionsResponse = await _client.GetAsync($"/api/projects/{ProjectId}/desktop/session-snapshots?taskId={_task.Id}&sourceInstanceId=desktop-a");
        sessionsResponse.EnsureSuccessStatusCode();
        var sessions = await sessionsResponse.Content.ReadFromJsonAsync<List<DesktopSessionSnapshot>>(JsonOpts);
        var session = Assert.Single(sessions!);
        Assert.Equal("pty-1", session.SessionId);
        Assert.True(session.ControlCapabilities!.Value.GetProperty("focus").GetBoolean());
    }

    private sealed class SnapshotAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-snapshot-api-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenMcp:DatabasePath"] = _dbPath,
                    ["DenMcp:Llm:Endpoint"] = "",
                    ["DenMcp:Llm:Model"] = "test-model"
                });
            });

            builder.ConfigureServices(services =>
            {
                var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
                initializer.InitializeAsync().GetAwaiter().GetResult();

                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(new NoOpLlmClient());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private sealed class NoOpLlmClient : ILlmClient
        {
            public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
                => Task.FromResult("{}");
        }
    }
}

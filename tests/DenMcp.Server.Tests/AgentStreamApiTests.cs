using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Server.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DenMcp.Server.Tests;

public class AgentStreamApiTests : IAsyncLifetime
{
    private const string ProjectId = "agent-stream-api-test";
    private const string OtherProjectId = "agent-stream-other";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private AgentStreamAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new AgentStreamAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Agent Stream API Test" });
        await projects.CreateAsync(new Project { Id = OtherProjectId, Name = "Other Project" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestAgentStream_GlobalAndProjectRoutesHonorFilters()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Message,
            EventType = "note",
            ProjectId = ProjectId,
            Sender = "user",
            DeliveryMode = AgentStreamDeliveryMode.Notify,
            Body = "FYI"
        });

        var projectOps = await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "review_requested",
            ProjectId = ProjectId,
            Sender = "codex",
            RecipientAgent = "claude-code",
            DeliveryMode = AgentStreamDeliveryMode.Wake
        });

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "review_requested",
            ProjectId = OtherProjectId,
            Sender = "claude-code",
            DeliveryMode = AgentStreamDeliveryMode.Wake
        });

        var globalResponse = await _client.GetAsync("/api/agent-stream?streamKind=ops&sender=codex");
        globalResponse.EnsureSuccessStatusCode();

        var globalEntries = await globalResponse.Content.ReadFromJsonAsync<List<AgentStreamEntry>>(JsonOpts);
        var globalEntry = Assert.Single(globalEntries!);
        Assert.Equal(projectOps.Id, globalEntry.Id);

        var projectResponse = await _client.GetAsync($"/api/projects/{ProjectId}/agent-stream?streamKind=ops");
        projectResponse.EnsureSuccessStatusCode();

        var projectEntries = await projectResponse.Content.ReadFromJsonAsync<List<AgentStreamEntry>>(JsonOpts);
        var projectEntry = Assert.Single(projectEntries!);
        Assert.Equal(projectOps.Id, projectEntry.Id);
        Assert.Equal(ProjectId, projectEntry.ProjectId);
    }

    [Fact]
    public async Task RestAgentStream_ProjectScopedGetRejectsEntryFromAnotherProject()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();

        var otherEntry = await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "wake_requested",
            ProjectId = OtherProjectId,
            Sender = "codex",
            DeliveryMode = AgentStreamDeliveryMode.Notify
        });

        var response = await _client.GetAsync($"/api/projects/{ProjectId}/agent-stream/{otherEntry.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task McpAgentStreamTools_ListAndGetSupportProjectScope()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();

        var entry = await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Message,
            EventType = "question",
            ProjectId = ProjectId,
            Sender = "user",
            RecipientAgent = "codex",
            DeliveryMode = AgentStreamDeliveryMode.Notify,
            Body = "Should I merge this?"
        });

        var listJson = await AgentStreamTools.ListAgentStream(
            repo,
            project_id: ProjectId,
            stream_kind: "message",
            recipient_agent: "codex");
        var list = JsonSerializer.Deserialize<List<AgentStreamEntry>>(listJson, JsonOpts);
        var listed = Assert.Single(list!);
        Assert.Equal(entry.Id, listed.Id);

        var getJson = await AgentStreamTools.GetAgentStreamEntry(repo, entry.Id, project_id: ProjectId);
        var fetched = JsonSerializer.Deserialize<AgentStreamEntry>(getJson, JsonOpts);
        Assert.NotNull(fetched);
        Assert.Equal("question", fetched!.EventType);

        var scopedMissJson = await AgentStreamTools.GetAgentStreamEntry(repo, entry.Id, project_id: OtherProjectId);
        Assert.Contains("not found", scopedMissJson, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AgentStreamAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-agent-stream-api-{Guid.NewGuid()}.db");

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
                var initializer = new DatabaseInitializer(_dbPath,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
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
                => Task.FromResult(string.Empty);
        }
    }
}

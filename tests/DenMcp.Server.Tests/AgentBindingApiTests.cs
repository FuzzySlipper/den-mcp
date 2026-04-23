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

public class AgentBindingApiTests : IAsyncLifetime
{
    private const string ProjectId = "agent-binding-api-test";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private AgentBindingAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new AgentBindingAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Agent Binding API Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestCheckIn_WithBindingFields_RegistersBinding()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/checkin", new
        {
            agent = "codex",
            project_id = ProjectId,
            session_id = "session-1",
            instance_id = "codex-reviewer-1",
            agent_family = "codex",
            role = "reviewer",
            transport_kind = "codex_app_server"
        });
        response.EnsureSuccessStatusCode();

        var bindingsResponse = await _client.GetAsync($"/api/agents/bindings?projectId={ProjectId}&role=reviewer");
        bindingsResponse.EnsureSuccessStatusCode();

        var bindings = await bindingsResponse.Content.ReadFromJsonAsync<List<AgentInstanceBinding>>(JsonOpts);
        var binding = Assert.Single(bindings!);
        Assert.Equal("codex-reviewer-1", binding.InstanceId);
        Assert.Equal("codex_app_server", binding.TransportKind);
    }

    [Fact]
    public async Task RestCheckIn_WithIncompleteBindingFields_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/checkin", new
        {
            agent = "codex",
            project_id = ProjectId,
            session_id = "session-2",
            instance_id = "codex-reviewer-2",
            role = "reviewer"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task McpAgentTools_ListBindings_ReturnsActiveBindings()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();

        await repo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "claude-reviewer-1",
            ProjectId = ProjectId,
            AgentIdentity = "claude-code",
            AgentFamily = "claude",
            Role = "reviewer",
            TransportKind = "claude_channel",
            Status = AgentInstanceBindingStatus.Active
        });

        var json = await AgentTools.ListAgentInstanceBindings(repo, project_id: ProjectId, role: "reviewer");
        var bindings = JsonSerializer.Deserialize<List<AgentInstanceBinding>>(json, JsonOpts);

        var binding = Assert.Single(bindings!);
        Assert.Equal("claude-reviewer-1", binding.InstanceId);
    }

    private sealed class AgentBindingAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-agent-binding-api-{Guid.NewGuid()}.db");

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

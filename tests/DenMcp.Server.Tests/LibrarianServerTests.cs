using System.Net;
using System.Net.Http.Json;
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

public class LibrarianServerTests
{
    [Fact]
    public async Task QueryRoute_WhenUnconfigured_ReturnsBadRequest()
    {
        using var factory = new LibrarianAppFactory(configureLlm: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/projects/proj/librarian/query", new
        {
            query = "anything"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", json);
        Assert.Contains("Librarian is not configured", json);
    }

    [Fact]
    public async Task QueryRoute_WhenTaskIsMissing_ReturnsNotFound()
    {
        using var factory = new LibrarianAppFactory(configureLlm: true);
        using var client = factory.CreateClient();
        await SeedProjectAsync(factory.Services, "proj");

        var response = await client.PostAsJsonAsync("/api/projects/proj/librarian/query", new
        {
            query = "anything",
            task_id = 99999
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, factory.StubLlmClient!.CallCount);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\":\"Task 99999 not found\"", json);
    }

    [Fact]
    public async Task QueryRoute_WhenTaskBelongsToAnotherProject_ReturnsBadRequest()
    {
        using var factory = new LibrarianAppFactory(configureLlm: true);
        using var client = factory.CreateClient();
        await SeedProjectAsync(factory.Services, "proj-a");
        var otherTaskId = await SeedProjectAndTaskAsync(factory.Services, "proj-b", "Other task");

        var response = await client.PostAsJsonAsync("/api/projects/proj-a/librarian/query", new
        {
            query = "anything",
            task_id = otherTaskId
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.StubLlmClient!.CallCount);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"error\":\"Task {otherTaskId} does not belong to project proj-a\"", json);
    }

    [Fact]
    public async Task QueryRoute_ReturnsSnakeCaseResponse()
    {
        const string llmResponse = """
            {
              "relevant_items": [
                {
                  "type": "document",
                  "source_id": "proj/fts-spec",
                  "project_id": "proj",
                  "summary": "FTS search spec",
                  "why_relevant": "Matches the current work",
                  "snippet": "SQLite FTS5 with porter stemmer."
                }
              ],
              "recommendations": ["Read the spec"],
              "confidence": "high"
            }
            """;

        using var factory = new LibrarianAppFactory(configureLlm: true, llmResponse);
        using var client = factory.CreateClient();
        await SeedProjectAsync(factory.Services, "proj");
        await SeedDocumentAsync(factory.Services, new Document
        {
            ProjectId = "proj",
            Slug = "fts-spec",
            Title = "FTS Spec",
            Content = "SQLite FTS5 with porter stemmer.",
            DocType = DocType.Spec
        });

        var response = await client.PostAsJsonAsync("/api/projects/proj/librarian/query", new
        {
            query = "porter stemmer"
        });

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, factory.StubLlmClient!.CallCount);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"relevant_items\"", json);
        Assert.Contains("\"source_id\"", json);
        Assert.Contains("\"why_relevant\"", json);
        Assert.DoesNotContain("\"RelevantItems\"", json);
    }

    [Fact]
    public async Task QueryLibrarianTool_ReturnsErrorJson_ForCrossProjectTask()
    {
        using var factory = new LibrarianAppFactory(configureLlm: true);
        await SeedProjectAsync(factory.Services, "proj-a");
        var otherTaskId = await SeedProjectAndTaskAsync(factory.Services, "proj-b", "Other task");

        using var scope = factory.Services.CreateScope();
        var librarian = scope.ServiceProvider.GetRequiredService<LibrarianService>();
        var llmConfig = scope.ServiceProvider.GetRequiredService<LlmConfig>();

        var json = await LibrarianTools.QueryLibrarian(librarian, llmConfig, "proj-a", "anything", otherTaskId);

        Assert.Equal(0, factory.StubLlmClient!.CallCount);
        Assert.Contains($"\"error\":\"Task {otherTaskId} does not belong to project proj-a\"", json);
    }

    private static async Task SeedProjectAsync(IServiceProvider services, string projectId)
    {
        using var scope = services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        if (await projects.GetByIdAsync(projectId) is not null)
            return;

        await projects.CreateAsync(new Project
        {
            Id = projectId,
            Name = projectId
        });
    }

    private static async Task<int> SeedProjectAndTaskAsync(IServiceProvider services, string projectId, string title)
    {
        using var scope = services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        if (await projects.GetByIdAsync(projectId) is null)
        {
            await projects.CreateAsync(new Project
            {
                Id = projectId,
                Name = projectId
            });
        }

        var task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = projectId,
            Title = title
        });

        return task.Id;
    }

    private static async Task SeedDocumentAsync(IServiceProvider services, Document document)
    {
        using var scope = services.CreateScope();
        var docs = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        await docs.UpsertAsync(document);
    }

    private sealed class LibrarianAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-server-test-{Guid.NewGuid()}.db");
        private readonly bool _configureLlm;

        public LibrarianAppFactory(bool configureLlm, string llmResponse = """{"relevant_items":[],"recommendations":[],"confidence":"low"}""")
        {
            _configureLlm = configureLlm;
            StubLlmClient = configureLlm ? new StubLlmClient(llmResponse) : null;
        }

        public StubLlmClient? StubLlmClient { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenMcp:DatabasePath"] = _dbPath,
                    ["DenMcp:Llm:Endpoint"] = _configureLlm ? "http://test" : "",
                    ["DenMcp:Llm:Model"] = "test-model",
                    ["DenMcp:Llm:MaxTokens"] = "128",
                    ["DenMcp:Llm:ContextTokenBudget"] = "512"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<LlmConfig>();
                services.AddSingleton(new LlmConfig
                {
                    Endpoint = _configureLlm ? "http://test" : "",
                    Model = "test-model",
                    MaxTokens = 128,
                    ContextTokenBudget = 512
                });

                services.RemoveAll<ILlmClient>();
                if (_configureLlm)
                    services.AddSingleton<ILlmClient>(StubLlmClient!);
                else
                    services.AddSingleton<ILlmClient>(new StubLlmClient("""{"relevant_items":[],"recommendations":[],"confidence":"low"}"""));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }

    private sealed class StubLlmClient : ILlmClient
    {
        private readonly string _response;

        public StubLlmClient(string response) => _response = response;

        public int CallCount { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_response);
        }
    }
}

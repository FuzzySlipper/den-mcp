using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DenMcp.Server.Tests;

public class MessageApiTests : IAsyncLifetime
{
    private const string ProjectId = "message-api-test";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private MessageAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new MessageAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Message API Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestMessages_RoundTripIntentAndFilter()
    {
        var postResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "codex",
            content = "Implementation note",
            intent = "note"
        });
        postResponse.EnsureSuccessStatusCode();

        var created = await postResponse.Content.ReadFromJsonAsync<Message>(JsonOpts);
        Assert.NotNull(created);
        Assert.Equal(MessageIntent.Note, created!.Intent);

        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "codex",
            content = "Review feedback",
            metadata = """{"type":"review_feedback","recipient":"claude-code"}"""
        });

        var getResponse = await _client.GetAsync($"/api/projects/{ProjectId}/messages?intent=note");
        getResponse.EnsureSuccessStatusCode();

        var messages = await getResponse.Content.ReadFromJsonAsync<List<Message>>(JsonOpts);
        var note = Assert.Single(messages!);
        Assert.Equal("Implementation note", note.Content);
        Assert.Equal(MessageIntent.Note, note.Intent);
    }

    [Fact]
    public async Task RestMessages_RejectConflictingIntentAndMetadataType()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "codex",
            content = "Conflicting handoff",
            intent = "review_request",
            metadata = """{"type":"review_feedback","recipient":"claude-code"}"""
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("conflicts", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestMessages_RejectUnknownIntentFilter()
    {
        var response = await _client.GetAsync($"/api/projects/{ProjectId}/messages?intent=not_real");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RestMessageFeed_ReturnsThreadSummaries()
    {
        var rootResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "alice",
            content = "Thread root"
        });
        rootResponse.EnsureSuccessStatusCode();
        var root = await rootResponse.Content.ReadFromJsonAsync<Message>(JsonOpts);

        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "carol",
            content = "Standalone note"
        });

        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "bob",
            content = "Thread reply",
            thread_id = root!.Id
        });

        var response = await _client.GetAsync($"/api/projects/{ProjectId}/messages/feed?limit=10");
        response.EnsureSuccessStatusCode();

        var feed = await response.Content.ReadFromJsonAsync<List<MessageFeedItem>>(JsonOpts);
        Assert.NotNull(feed);
        Assert.Equal(2, feed!.Count);

        var threadItem = Assert.Single(feed, item => item.RootMessage.Id == root.Id);
        Assert.Equal(root.Id, threadItem.RootMessage.Id);
        Assert.Equal(1, threadItem.ReplyCount);
        Assert.Equal("Thread reply", threadItem.LatestMessage.Content);
    }

    [Fact]
    public async Task McpMessageTools_SendAndGetMessages_SupportIntent()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MessageTools>>();

        var createdJson = await MessageTools.SendMessage(
            repo,
            detection,
            logger,
            ProjectId,
            "codex",
            "Canonical handoff",
            intent: "handoff");

        var created = JsonSerializer.Deserialize<Message>(createdJson, JsonOpts);
        Assert.NotNull(created);
        Assert.Equal(MessageIntent.Handoff, created!.Intent);

        await MessageTools.SendMessage(
            repo,
            detection,
            logger,
            ProjectId,
            "codex",
            "General chat");

        var filteredJson = await MessageTools.GetMessages(repo, ProjectId, intent: "handoff");
        var filtered = JsonSerializer.Deserialize<List<Message>>(filteredJson, JsonOpts);

        var handoff = Assert.Single(filtered!);
        Assert.Equal("Canonical handoff", handoff.Content);
        Assert.Equal(MessageIntent.Handoff, handoff.Intent);
    }

    private sealed class MessageAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-message-api-{Guid.NewGuid()}.db");

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
                => Task.FromResult("{}");
        }
    }
}

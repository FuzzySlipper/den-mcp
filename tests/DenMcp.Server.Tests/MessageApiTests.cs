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
    public async Task RestMessageById_ReturnsMessageWithinProject()
    {
        var postResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "alice",
            content = "Source-attributed librarian message"
        });
        postResponse.EnsureSuccessStatusCode();
        var created = await postResponse.Content.ReadFromJsonAsync<Message>(JsonOpts);

        var getResponse = await _client.GetAsync($"/api/projects/{ProjectId}/messages/{created!.Id}");
        getResponse.EnsureSuccessStatusCode();

        var loaded = await getResponse.Content.ReadFromJsonAsync<Message>(JsonOpts);
        Assert.Equal(created.Id, loaded!.Id);
        Assert.Equal("Source-attributed librarian message", loaded.Content);
    }

    [Fact]
    public async Task RestMessageById_Returns404ForWrongProject()
    {
        var postResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "alice",
            content = "Project-scoped message"
        });
        postResponse.EnsureSuccessStatusCode();
        var created = await postResponse.Content.ReadFromJsonAsync<Message>(JsonOpts);

        var getResponse = await _client.GetAsync($"/api/projects/other-project/messages/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
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
    public async Task RestMessageFeed_FiltersByIntent()
    {
        var rootResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "alice",
            content = "General thread root"
        });
        rootResponse.EnsureSuccessStatusCode();
        var root = await rootResponse.Content.ReadFromJsonAsync<Message>(JsonOpts);

        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "bob",
            content = "Review feedback reply",
            thread_id = root!.Id,
            metadata = """{"type":"review_feedback","recipient":"claude-code"}"""
        });

        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "carol",
            content = "Standalone note",
            intent = "note"
        });

        var response = await _client.GetAsync($"/api/projects/{ProjectId}/messages/feed?intent=review_feedback");
        response.EnsureSuccessStatusCode();

        var feed = await response.Content.ReadFromJsonAsync<List<MessageFeedItem>>(JsonOpts);
        var item = Assert.Single(feed!);
        Assert.Equal(root.Id, item.RootMessage.Id);
        Assert.Equal("Review feedback reply", item.LatestMessage.Content);
        Assert.Equal(MessageIntent.ReviewFeedback, item.LatestMessage.Intent);
    }

    [Fact]
    public async Task RestMessageFeed_RejectsUnknownIntentFilter()
    {
        var response = await _client.GetAsync($"/api/projects/{ProjectId}/messages/feed?intent=not_real");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
            intent: "handoff",
            verbose: true);

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

    [Fact]
    public async Task McpMessageTools_MetadataAcceptsObjectOrString()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MessageTools>>();

        // Object input path — the natural way agents want to pass metadata
        var objectJson = await MessageTools.SendMessage(
            repo, detection, logger,
            ProjectId, "pi", "Object metadata test",
            metadata: JsonSerializer.Deserialize<JsonElement>("""{"type":"coder_context_packet","version":1}"""),
            verbose: true);

        var objectMsg = JsonSerializer.Deserialize<Message>(objectJson, JsonOpts);
        Assert.NotNull(objectMsg);
        Assert.Equal("coder_context_packet", objectMsg!.Metadata?.GetProperty("type").GetString());
        Assert.Equal(1, objectMsg.Metadata?.GetProperty("version").GetInt32());

        // String input path — backward compatible with existing callers
        var stringJson = await MessageTools.SendMessage(
            repo, detection, logger,
            ProjectId, "pi", "String metadata test",
            metadata: JsonSerializer.Deserialize<JsonElement>("""{"type":"review_request","recipient":"claude-code"}"""),
            verbose: true);

        var stringMsg = JsonSerializer.Deserialize<Message>(stringJson, JsonOpts);
        Assert.NotNull(stringMsg);
        Assert.Equal("review_request", stringMsg!.Metadata?.GetProperty("type").GetString());

        // Null input path — no metadata
        var nullJson = await MessageTools.SendMessage(
            repo, detection, logger,
            ProjectId, "pi", "No metadata test",
            verbose: true);

        var nullMsg = JsonSerializer.Deserialize<Message>(nullJson, JsonOpts);
        Assert.NotNull(nullMsg);
        Assert.Null(nullMsg!.Metadata);
    }

    [Fact]
    public async Task PacketTools_PrepareCoderContextPacket_StoresSearchableTaskMessage()
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = ProjectId,
            Title = "Implement packet flow",
            Description = "Acceptance: bounded references only",
            Tags = ["prompt-packets"]
        });
        await messages.CreateAsync(new Message
        {
            ProjectId = ProjectId,
            TaskId = task.Id,
            Sender = "planner",
            Content = "Use references, not process args.",
            Intent = MessageIntent.Handoff
        });

        var json = await PacketTools.PrepareCoderContextPacket(
            tasks,
            messages,
            ProjectId,
            task.Id,
            requested_by: "hermes",
            branch: "task/1240-prompt-packet-reference-flow",
            base_branch: "main",
            base_commit: "abc123",
            allowed_scope: "src/DenMcp.Server/Tools, tests/DenMcp.Server.Tests",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var packet = doc.RootElement.GetProperty("packet");
        Assert.Equal("coder_context_packet", packet.GetProperty("type").GetString());
        Assert.Equal("coder", packet.GetProperty("role").GetString());
        var messageId = packet.GetProperty("message_id").GetInt32();
        Assert.True(messageId > 0);
        Assert.Contains("Implement packet flow", packet.GetProperty("content").GetString());
        Assert.Contains("bounded references", packet.GetProperty("content").GetString());
        Assert.Contains("Prompt-injection", packet.GetProperty("content").GetString());

        var latestJson = await PacketTools.GetLatestTaskPacket(messages, ProjectId, task.Id, packet_type: "coder_context_packet", verbose: true);
        using var latestDoc = JsonDocument.Parse(latestJson);
        var latest = latestDoc.RootElement.GetProperty("packet");
        Assert.Equal(messageId, latest.GetProperty("message_id").GetInt32());
        Assert.Equal("coder_context_packet", latest.GetProperty("metadata").GetProperty("type").GetString());
        Assert.Equal("task/1240-prompt-packet-reference-flow", latest.GetProperty("metadata").GetProperty("branch").GetString());

        var threadMessages = await messages.GetMessagesAsync(ProjectId, taskId: task.Id, limit: 10);
        var stored = Assert.Single(threadMessages, m => m.Id == messageId);
        Assert.Equal(MessageIntent.Handoff, stored.Intent);
        Assert.Equal("coder_context_packet", stored.Metadata?.GetProperty("type").GetString());
    }

    [Fact]
    public async Task PacketTools_RenderWorkerPrompt_UsesPacketReferenceNotPacketBody()
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = ProjectId,
            Title = "Long prompt task",
            Description = new string('x', 5000)
        });

        var packetJson = await PacketTools.PrepareReviewerContextPacket(
            tasks,
            messages,
            ProjectId,
            task.Id,
            requested_by: "hermes",
            review_round_id: 77,
            branch: "task/review-me",
            head_commit: "def456",
            verbose: true);
        using var packetDoc = JsonDocument.Parse(packetJson);
        var messageId = packetDoc.RootElement.GetProperty("packet").GetProperty("message_id").GetInt32();

        var promptJson = await PacketTools.RenderWorkerPrompt(messages, ProjectId, messageId, role: "reviewer", verbose: true);
        using var promptDoc = JsonDocument.Parse(promptJson);
        var prompt = promptDoc.RootElement.GetProperty("prompt").GetString();
        Assert.NotNull(prompt);
        Assert.Contains($"message #{messageId}", prompt);
        Assert.Contains("get_thread", prompt);
        Assert.Contains("reviewer_context_packet", prompt);
        Assert.DoesNotContain(new string('x', 100), prompt);
        Assert.True(prompt!.Length < 1600);
    }

    [Fact]
    public async Task McpMessageTools_SendUserNotification_CreatesNotificationMessage()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MessageTools>>();

        var json = await MessageTools.SendUserNotification(
            repo, detection, logger,
            ProjectId, "pi", "Server redeployment required",
            urgency: "high",
            verbose: true);

        var msg = JsonSerializer.Deserialize<Message>(json, JsonOpts);
        Assert.NotNull(msg);
        Assert.Equal(MessageIntent.Notification, msg!.Intent);
        Assert.Equal("high", msg.Metadata?.GetProperty("urgency").GetString());
        Assert.Equal("pi", msg.Metadata?.GetProperty("source_sender").GetString());
        Assert.Equal("Server redeployment required", msg.Content);

        // Verify it appears in notification-filtered messages
        var filteredJson = await MessageTools.GetMessages(repo, ProjectId, intent: "notification");
        var filtered = JsonSerializer.Deserialize<List<Message>>(filteredJson, JsonOpts);
        var notification = Assert.Single(filtered!);
        Assert.Equal(msg.Id, notification.Id);
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

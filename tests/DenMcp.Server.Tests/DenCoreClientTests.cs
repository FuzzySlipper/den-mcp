using System.Net;
using System.Text;
using System.Text.Json;
using DenMcp.Core.Models;
using DenMcp.Server.CoreClient;
using DenMcp.Server.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenMcp.Server.Tests;

public sealed class DenCoreClientTests
{
    [Fact]
    public async Task ListProjects_ReturnsRetryableError_ForConnectionRefused()
    {
        using var httpClient = new HttpClient(new ThrowingHandler(new HttpRequestException("connection refused")))
        {
            BaseAddress = new Uri("http://127.0.0.1:1")
        };
        var client = new DenCoreClient(httpClient, new DenCoreOptions { BaseUrl = "http://127.0.0.1:1", TimeoutSeconds = 1 });

        var result = await ProjectTools.ListProjects(client);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal("den_core_unavailable", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Equal("list_projects", doc.RootElement.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task GetProject_ReturnsRetryableError_ForServiceUnavailable()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(HttpStatusCode.ServiceUnavailable, "core rebooting"))
        {
            BaseAddress = new Uri("http://den-core.test")
        };
        var client = new DenCoreClient(httpClient, new DenCoreOptions { BaseUrl = "http://den-core.test" });

        var result = await ProjectTools.GetProject(client, "den-mcp");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal("den_core_unavailable", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Equal(503, doc.RootElement.GetProperty("status_code").GetInt32());
    }

    [Fact]
    public async Task GetProject_ReturnsNonRetryableError_ForBadRequest()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(HttpStatusCode.BadRequest, "bad project id"))
        {
            BaseAddress = new Uri("http://den-core.test")
        };
        var client = new DenCoreClient(httpClient, new DenCoreOptions { BaseUrl = "http://den-core.test" });

        var result = await ProjectTools.GetProject(client, "bad id");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal("den_core_error", doc.RootElement.GetProperty("error").GetString());
        Assert.False(doc.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Equal(400, doc.RootElement.GetProperty("status_code").GetInt32());
    }

    [Fact]
    public async Task CreateProject_PreservesToolOutputShape_WhenCoreSucceeds()
    {
        var project = new Project { Id = "new-project", Name = "New Project", Description = "desc" };
        using var httpClient = new HttpClient(new JsonResponseHandler<Project>(HttpStatusCode.Created, project))
        {
            BaseAddress = new Uri("http://den-core.test")
        };
        var client = new DenCoreClient(httpClient, new DenCoreOptions { BaseUrl = "http://den-core.test" });

        var concise = await ProjectTools.CreateProject(client, "new-project", "New Project", description: "desc", verbose: false);
        var verbose = await ProjectTools.CreateProject(client, "new-project", "New Project", description: "desc", verbose: true);

        using var conciseDoc = JsonDocument.Parse(concise);
        Assert.Equal("created project 'new-project'", conciseDoc.RootElement.GetProperty("summary").GetString());
        Assert.Equal("new-project", conciseDoc.RootElement.GetProperty("id").GetString());
        using var doc = JsonDocument.Parse(verbose);
        Assert.Equal("new-project", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task DocumentTools_StoreDocument_PostsToCoreProjectDocumentsApi()
    {
        var document = new Document
        {
            Id = 42,
            ProjectId = "den-mcp",
            Slug = "single-writer-note",
            Title = "Single writer note",
            Content = "body",
            DocType = DocType.Note,
            Tags = ["sqlite", "core"]
        };
        var handler = new CaptureJsonResponseHandler<Document>(HttpStatusCode.Created, document);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://den-core.test")
        };
        var client = new DenCoreClient(httpClient, new DenCoreOptions { BaseUrl = "http://den-core.test" });

        var result = await DocumentTools.StoreDocument(
            client,
            "den-mcp",
            "single-writer-note",
            "Single writer note",
            "body",
            doc_type: "note",
            tags: "[\"sqlite\",\"core\"]",
            verbose: true);

        Assert.Equal("/api/projects/den-mcp/documents/", handler.LastRequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("single-writer-note", requestJson.RootElement.GetProperty("slug").GetString());
        Assert.Equal("note", requestJson.RootElement.GetProperty("doc_type").GetString());
        Assert.Equal("sqlite", requestJson.RootElement.GetProperty("tags")[0].GetString());
        using var resultJson = JsonDocument.Parse(result);
        Assert.Equal("single-writer-note", resultJson.RootElement.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task MessageTools_SendMessage_PostsSnakeCaseBodyToCoreMessagesApi()
    {
        var message = new Message
        {
            Id = 99,
            ProjectId = "den-mcp",
            TaskId = 1359,
            ThreadId = 5715,
            Sender = "sysadmin",
            Content = "via core",
            Intent = MessageIntent.Handoff
        };
        var handler = new CaptureJsonResponseHandler<Message>(HttpStatusCode.Created, message);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://den-core.test")
        };
        var client = new DenCoreClient(httpClient, new DenCoreOptions { BaseUrl = "http://den-core.test" });

        var result = await MessageTools.SendMessage(
            client,
            "den-mcp",
            "sysadmin",
            "via core",
            task_id: 1359,
            thread_id: 5715,
            metadata: JsonSerializer.Deserialize<JsonElement>("{\"k\":\"v\"}"),
            intent: "handoff",
            verbose: true);

        Assert.Equal("/api/projects/den-mcp/messages/", handler.LastRequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("sysadmin", requestJson.RootElement.GetProperty("sender").GetString());
        Assert.Equal(1359, requestJson.RootElement.GetProperty("task_id").GetInt32());
        Assert.Equal(5715, requestJson.RootElement.GetProperty("thread_id").GetInt32());
        Assert.Equal("handoff", requestJson.RootElement.GetProperty("intent").GetString());
        Assert.Equal("v", requestJson.RootElement.GetProperty("metadata").GetProperty("k").GetString());
        using var resultJson = JsonDocument.Parse(result);
        Assert.Equal(99, resultJson.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task AdapterMode_StartsWithoutCreatingLocalDatabaseFile()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-adapter-mode-{Guid.NewGuid()}", "should-not-exist.db");
        await using var factory = new AdapterModeFactory(dbPath);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.False(File.Exists(dbPath));
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain")
            });
    }

    private sealed class JsonResponseHandler<T>(HttpStatusCode statusCode, T body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts.Default), Encoding.UTF8, "application/json")
            });
    }

    private sealed class CaptureJsonResponseHandler<T>(HttpStatusCode statusCode, T body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts.Default), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class AdapterModeFactory(string dbPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["db-path"] = dbPath,
                    ["DenMcp:LocalDatabaseEnabled"] = "false",
                    ["DenCore:BaseUrl"] = "http://127.0.0.1:1",
                    ["DenCore:TimeoutSeconds"] = "1",
                    ["llm-endpoint"] = "http://localhost/fake",
                    ["llm-api-key"] = "test-key",
                    ["llm-model"] = "fake"
                });
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}

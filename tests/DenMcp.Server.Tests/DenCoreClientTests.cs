using System.Net;
using System.Text;
using System.Text.Json;
using DenMcp.Core.Models;
using DenMcp.Server.CoreClient;
using DenMcp.Server.Tools;

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

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DenMcp.Core.Models;

namespace DenMcp.Server.CoreClient;

public sealed class DenCoreClient
{
    private readonly HttpClient _httpClient;
    private readonly DenCoreOptions _options;

    public DenCoreClient(HttpClient httpClient, DenCoreOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _httpClient.BaseAddress ??= new Uri(NormalizeBaseUrl(options.BaseUrl));
        _httpClient.Timeout = options.Timeout;
        if (!string.IsNullOrWhiteSpace(options.ServiceToken))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceToken);
    }

    public string CoreUrl => _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? NormalizeBaseUrl(_options.BaseUrl).TrimEnd('/');

    public Task<Project> CreateProjectAsync(Project project, CancellationToken cancellationToken = default) =>
        SendAsync<Project>(HttpMethod.Post, "/api/projects/", "create_project", project, cancellationToken);

    public Task<JsonElement> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Get, "/api/projects/", "list_projects", body: null, cancellationToken);

    public Task<JsonElement> GetProjectAsync(string projectId, string? agent = null, CancellationToken cancellationToken = default)
    {
        var path = $"/api/projects/{Uri.EscapeDataString(projectId)}";
        if (!string.IsNullOrWhiteSpace(agent))
            path += $"?agent={Uri.EscapeDataString(agent)}";
        return SendAsync<JsonElement>(HttpMethod.Get, path, "get_project", body: null, cancellationToken);
    }

    public async Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<JsonElement>(HttpMethod.Get, "/health", "core_health", body: null, cancellationToken);

    public Task<Document> StoreDocumentAsync(string projectId, object body, CancellationToken cancellationToken = default) =>
        SendAsync<Document>(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/documents/", "store_document", body, cancellationToken);

    public Task<JsonElement> GetDocumentAsync(string projectId, string slug, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Get, $"/api/projects/{Uri.EscapeDataString(projectId)}/documents/{Uri.EscapeDataString(slug)}", "get_document", body: null, cancellationToken);

    public Task<JsonElement> ListDocumentsAsync(string? projectId = null, string? docType = null, string? tags = null, CancellationToken cancellationToken = default)
    {
        var query = BuildQuery([
            ("projectId", projectId),
            ("doc_type", docType),
            ("tags", tags)
        ]);
        return SendAsync<JsonElement>(HttpMethod.Get, $"/api/documents{query}", "list_documents", body: null, cancellationToken);
    }

    public Task<JsonElement> SearchDocumentsAsync(string query, string? projectId = null, CancellationToken cancellationToken = default)
    {
        var qs = BuildQuery([
            ("query", query),
            ("projectId", projectId)
        ]);
        return SendAsync<JsonElement>(HttpMethod.Get, $"/api/documents/search{qs}", "search_documents", body: null, cancellationToken);
    }

    public Task<JsonElement> DeleteDocumentAsync(string projectId, string slug, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Delete, $"/api/projects/{Uri.EscapeDataString(projectId)}/documents/{Uri.EscapeDataString(slug)}", "delete_document", body: null, cancellationToken);

    public Task<Message> SendMessageAsync(string projectId, object body, CancellationToken cancellationToken = default) =>
        SendAsync<Message>(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/messages/", "send_message", body, cancellationToken);

    public Task<JsonElement> GetMessagesAsync(string projectId, int? taskId = null, string? since = null, string? unreadFor = null, int? limit = null, string? intent = null, CancellationToken cancellationToken = default)
    {
        var query = BuildQuery([
            ("taskId", taskId?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("since", since),
            ("unreadFor", unreadFor),
            ("limit", limit?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("intent", intent)
        ]);
        return SendAsync<JsonElement>(HttpMethod.Get, $"/api/projects/{Uri.EscapeDataString(projectId)}/messages/{query}", "get_messages", body: null, cancellationToken);
    }

    public Task<JsonElement> GetThreadAsync(string projectId, int threadId, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Get, $"/api/projects/{Uri.EscapeDataString(projectId)}/messages/thread/{threadId}", "get_thread", body: null, cancellationToken);

    public Task<JsonElement> MarkReadAsync(string agent, int[] messageIds, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "/api/messages/mark-read", "mark_read", new { agent, message_ids = messageIds }, cancellationToken);

    public async Task<T> SendAsync<T>(HttpMethod method, string path, string operation, object? body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts.Default), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DenCoreException(operation, CoreUrl, $"Den Core did not respond within {_options.Timeout.TotalSeconds:0.#}s; MCP transport is still alive. Retry after Core is healthy.", retryable: true, innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DenCoreException(operation, CoreUrl, $"Den Core is unreachable: {ex.Message}", retryable: true, innerException: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = response.Content is null ? null : await response.Content.ReadAsStringAsync(cancellationToken);
                throw new DenCoreException(
                    operation,
                    CoreUrl,
                    BuildErrorMessage(response.StatusCode, responseBody),
                    IsRetryableStatusCode(response.StatusCode),
                    response.StatusCode,
                    responseBody);
            }

            var result = await response.Content.ReadFromJsonAsync<T>(JsonOpts.Default, cancellationToken);
            return result is null
                ? throw new DenCoreException(operation, CoreUrl, "Den Core returned an empty response.", retryable: true, responseBody: null)
                : result;
        }
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout or
        HttpStatusCode.InternalServerError;

    private static string BuildErrorMessage(HttpStatusCode statusCode, string? body)
    {
        var hint = (int)statusCode switch
        {
            400 or 404 => "Den Core rejected the request; fix the input and retry.",
            401 or 403 => "Den Core rejected MCP adapter credentials; check DenCore:ServiceToken configuration.",
            502 or 503 or 504 => "Den Core is temporarily unavailable; MCP transport is still alive. Retry after Core is healthy.",
            _ => "Den Core returned an error."
        };

        if (string.IsNullOrWhiteSpace(body))
            return $"{hint} HTTP {(int)statusCode} ({statusCode}).";
        return $"{hint} HTTP {(int)statusCode} ({statusCode}): {Truncate(body.Trim(), 600)}";
    }

    private static string NormalizeBaseUrl(string baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:5199" : baseUrl.TrimEnd('/');

    private static string BuildQuery(IEnumerable<(string Key, string? Value)> values)
    {
        var parts = values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}")
            .ToArray();
        return parts.Length == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}

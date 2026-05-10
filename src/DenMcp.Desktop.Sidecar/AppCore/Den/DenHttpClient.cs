using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

public sealed record DenSpaceListOptions
{
    public bool IncludeHidden { get; init; }
    public bool IncludeArchived { get; init; }

    public static DenSpaceListOptions FromSettings(OperatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new DenSpaceListOptions
        {
            IncludeHidden = settings.IncludeHiddenSpaces,
            IncludeArchived = settings.IncludeArchivedSpaces,
        };
    }
}

public sealed class DenHttpClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonSerializerOptions();

    private readonly HttpClient _httpClient;

    public DenHttpClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public static HttpClient CreateHttpClient()
    {
        return new HttpClient { Timeout = DefaultTimeout };
    }

    public async Task<DenHealth> HealthAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, JoinUrl(baseUrl, "/health")),
            "Den health check failed",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new DenHttpClientException($"Den health check returned HTTP {(int)response.StatusCode}");
            }

            return await ReadJsonAsync<DenHealth>(
                response,
                "Unable to parse Den health response",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DenProject>> ListProjectsAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, JoinUrl(baseUrl, "/api/projects")),
            "Unable to fetch Den projects",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new DenHttpClientException($"Den projects request returned HTTP {(int)response.StatusCode}");
            }

            return await ReadJsonAsync<List<DenProject>>(
                response,
                "Unable to parse Den projects",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DenSpace>> ListSpacesAsync(
        string baseUrl,
        DenSpaceListOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var url = BuildUrl(
            baseUrl,
            "/api/spaces",
            new[]
            {
                new QueryParameter("includeHidden", options.IncludeHidden ? "true" : "false"),
                new QueryParameter("includeArchived", options.IncludeArchived ? "true" : "false"),
            });
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            "Unable to fetch Den spaces",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new DenHttpClientException($"Den spaces request returned HTTP {(int)response.StatusCode}");
            }

            return await ReadJsonAsync<List<DenSpace>>(
                response,
                "Unable to parse Den spaces",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DenAgentWorkspace>> ListAgentWorkspacesAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(
            baseUrl,
            "/api/agent-workspaces",
            new[] { new QueryParameter("limit", "200") });
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            "Unable to fetch Den agent workspaces",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new DenHttpClientException($"Den agent workspaces request returned HTTP {(int)response.StatusCode}");
            }

            return await ReadJsonAsync<List<DenAgentWorkspace>>(
                response,
                "Unable to parse Den agent workspaces",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task PublishGitSnapshotAsync(
        string baseUrl,
        string projectId,
        DesktopGitSnapshotRequest snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return PublishSnapshotAsync(
            baseUrl,
            projectId,
            "/desktop/git-snapshots",
            snapshot,
            "git",
            cancellationToken);
    }

    public Task PublishDiffSnapshotAsync(
        string baseUrl,
        string projectId,
        DesktopDiffSnapshotRequest snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return PublishSnapshotAsync(
            baseUrl,
            projectId,
            "/desktop/diff-snapshots",
            snapshot,
            "diff",
            cancellationToken);
    }

    public Task PublishSessionSnapshotAsync(
        string baseUrl,
        string projectId,
        DesktopSessionSnapshotRequest snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return PublishSnapshotAsync(
            baseUrl,
            projectId,
            "/desktop/session-snapshots",
            snapshot,
            "session",
            cancellationToken);
    }

    public async Task PublishSessionEventAsync(
        string baseUrl,
        string projectId,
        AppendDesktopSessionEventRequest sessionEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var path = $"/api/projects/{EscapePathSegment(projectId)}/desktop/session-events";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, JoinUrl(baseUrl, path))
            {
                Content = JsonContent(sessionEvent),
            },
            $"Unable to publish desktop session event for {projectId}",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException(
                    $"Desktop session event publish for {projectId} returned HTTP {(int)response.StatusCode}: {body}");
            }
        }
    }

    public async Task<DesktopDiffSnapshotLatestResult> LatestDiffSnapshotAsync(
        string baseUrl,
        LatestDiffSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = new List<QueryParameter>
        {
            new("sourceInstanceId", request.SourceInstanceId),
            new("rootPath", request.RootPath),
            new("staleAfterSeconds", "120"),
        };
        AddNonBlank(query, "path", request.Path);
        AddNonBlank(query, "workspaceId", request.WorkspaceId);
        if (request.TaskId is { } taskId)
        {
            query.Add(new QueryParameter("taskId", taskId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var path = $"/api/projects/{EscapePathSegment(request.ProjectId)}/desktop/diff-snapshots/latest";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BuildUrl(baseUrl, path, query)),
            "Unable to fetch latest desktop diff snapshot",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException(
                    $"Latest desktop diff snapshot returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<DesktopDiffSnapshotLatestResult>(
                response,
                "Unable to parse desktop diff snapshot response",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DenTaskRecord>> ListTasksAsync(
        string baseUrl,
        string projectId,
        long? parentId = null,
        bool tree = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var query = new List<QueryParameter>();
        if (parentId is { } id)
        {
            query.Add(new QueryParameter("parentId", id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (tree)
        {
            query.Add(new QueryParameter("tree", "true"));
        }

        var path = $"/api/projects/{EscapePathSegment(projectId)}/tasks";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BuildUrl(baseUrl, path, query)),
            "Unable to fetch Den tasks",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Den tasks request returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<List<DenTaskRecord>>(
                response,
                "Unable to parse Den tasks",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DenTaskDetail> GetTaskDetailAsync(
        string baseUrl,
        string projectId,
        long taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var path = $"/api/projects/{EscapePathSegment(projectId)}/tasks/{taskId}";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, JoinUrl(baseUrl, path)),
            $"Unable to fetch Den task {taskId}",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Den task {taskId} returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<DenTaskDetail>(
                response,
                $"Unable to parse Den task {taskId}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DenMessage>> ListMessagesAsync(
        string baseUrl,
        string projectId,
        long? taskId = null,
        int limit = 20,
        string? unreadFor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var query = new List<QueryParameter>
        {
            new("limit", Math.Clamp(limit, 1, 100).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (taskId is { } id)
        {
            query.Add(new QueryParameter("taskId", id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (!string.IsNullOrWhiteSpace(unreadFor))
        {
            query.Add(new QueryParameter("unreadFor", unreadFor));
        }

        var path = $"/api/projects/{EscapePathSegment(projectId)}/messages";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BuildUrl(baseUrl, path, query)),
            "Unable to fetch Den messages",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Den messages request returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<List<DenMessage>>(
                response,
                "Unable to parse Den messages",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DenMessage?> GetMessageAsync(
        string baseUrl,
        string projectId,
        long messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var path = $"/api/projects/{EscapePathSegment(projectId)}/messages/{messageId}";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, JoinUrl(baseUrl, path)),
            $"Unable to fetch Den message {messageId}",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Den message {messageId} returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<DenMessage>(
                response,
                $"Unable to parse Den message {messageId}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DenSubagentRunSummary>> ListSubagentRunsAsync(
        string baseUrl,
        string projectId,
        long? taskId = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var query = new List<QueryParameter>
        {
            new("limit", Math.Clamp(limit, 1, 50).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (taskId is { } id)
        {
            query.Add(new QueryParameter("taskId", id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var path = $"/api/projects/{EscapePathSegment(projectId)}/subagent-runs";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BuildUrl(baseUrl, path, query)),
            "Unable to fetch Den sub-agent runs",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Den sub-agent runs request returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<List<DenSubagentRunSummary>>(
                response,
                "Unable to parse Den sub-agent runs",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DenAgentStreamEntry>> ListAgentStreamAsync(
        string baseUrl,
        string projectId,
        long? taskId = null,
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var query = new List<QueryParameter>
        {
            new("limit", Math.Clamp(limit, 1, 100).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (taskId is { } id)
        {
            query.Add(new QueryParameter("taskId", id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var path = $"/api/projects/{EscapePathSegment(projectId)}/agent-stream";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BuildUrl(baseUrl, path, query)),
            "Unable to fetch Den agent stream",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Den agent stream request returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<List<DenAgentStreamEntry>>(
                response,
                "Unable to parse Den agent stream",
                cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Task update API method (task #1152) ────────────────────────────────────

    public async Task<DenTaskRecord> UpdateTaskAsync(
        string baseUrl,
        string projectId,
        long taskId,
        DenTaskUpdateRequest update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(update);

        var path = $"/api/projects/{EscapePathSegment(projectId)}/tasks/{taskId}";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, JoinUrl(baseUrl, path))
            {
                Content = JsonContent(update),
            },
            $"Unable to update Den task {taskId}",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Den task {taskId} update returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<DenTaskRecord>(
                response,
                $"Unable to parse updated Den task {taskId}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Document API methods (task #1147) ────────────────────────────────────

    public async Task<IReadOnlyList<DenDocumentSummary>> ListDocumentsAsync(
        string baseUrl,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var path = projectId is not null
            ? $"/api/projects/{EscapePathSegment(projectId)}/documents"
            : "/api/documents";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, JoinUrl(baseUrl, path)),
            "Unable to fetch Den documents",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Den documents list returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<List<DenDocumentSummary>>(
                response,
                "Unable to parse Den documents",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DenDocumentDetail> GetDocumentAsync(
        string baseUrl,
        string projectId,
        string slug,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var path = $"/api/projects/{EscapePathSegment(projectId)}/documents/{Uri.EscapeDataString(slug)}";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, JoinUrl(baseUrl, path)),
            $"Unable to fetch Den document '{slug}'",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Den document '{slug}' returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<DenDocumentDetail>(
                response,
                $"Unable to parse Den document '{slug}'",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DenDocumentDetail> StoreDocumentAsync(
        string baseUrl,
        string projectId,
        StoreDocumentApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var path = $"/api/projects/{EscapePathSegment(projectId)}/documents";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, JoinUrl(baseUrl, path))
            {
                Content = JsonContent(request),
            },
            $"Unable to store Den document '{request.Slug}'",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Den document store returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<DenDocumentDetail>(
                response,
                $"Unable to parse stored Den document",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<CollaborationSessionData> GetCollaborationSessionAsync(
        string baseUrl,
        long sessionId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, JoinUrl(baseUrl, $"/api/projects/_/collaboration/sessions/{sessionId}")),
            $"Unable to fetch collaboration session {sessionId}",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Collaboration session {sessionId} returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<CollaborationSessionData>(
                response,
                $"Unable to parse collaboration session {sessionId}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<CollaborationDraftRecord> CreateCollaborationDraftAsync(
        string baseUrl,
        string projectId,
        long sessionId,
        CreateCollaborationDraftApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var path = $"/api/projects/{EscapePathSegment(projectId)}/collaboration/sessions/{sessionId}/drafts";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, JoinUrl(baseUrl, path))
            {
                Content = JsonContent(request),
            },
            $"Unable to create collaboration draft for session {sessionId}",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException($"Collaboration draft create returned HTTP {(int)response.StatusCode}: {body}");
            }

            return await ReadJsonAsync<CollaborationDraftRecord>(
                response,
                "Unable to parse collaboration draft response",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public static JsonSerializerOptions CreateJsonSerializerOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new DesktopSnapshotStateJsonConverter() },
        };
    }

    private async Task PublishSnapshotAsync<TSnapshot>(
        string baseUrl,
        string projectId,
        string snapshotRoute,
        TSnapshot snapshot,
        string snapshotKind,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var path = $"/api/projects/{EscapePathSegment(projectId)}{snapshotRoute}";
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, JoinUrl(baseUrl, path))
            {
                Content = JsonContent(snapshot),
            },
            $"Unable to publish desktop {snapshotKind} snapshot for {projectId}",
            cancellationToken).ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DenHttpClientException(
                    $"Desktop {snapshotKind} snapshot publish for {projectId} returned HTTP {(int)response.StatusCode}: {body}");
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        using var request = requestFactory();
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransportException(ex, cancellationToken))
        {
            throw new DenHttpClientException($"{errorPrefix}: {ex.Message}", ex);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return value ?? throw new JsonException("Response body was empty.");
        }
        catch (JsonException ex)
        {
            throw new DenHttpClientException($"{errorPrefix}: {ex.Message}", ex);
        }
    }

    private static StringContent JsonContent<T>(T value)
    {
        return new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Uri BuildUrl(string baseUrl, string path, IReadOnlyCollection<QueryParameter> query)
    {
        var url = JoinUrl(baseUrl, path);
        if (query.Count == 0)
        {
            return url;
        }

        var builder = new UriBuilder(url)
        {
            Query = string.Join("&", query.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value)}")),
        };
        return builder.Uri;
    }

    private static Uri JoinUrl(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new DenHttpClientException($"Invalid Den server URL '{baseUrl}': URI is not absolute.");
        }

        if (baseUri.Scheme is not ("http" or "https"))
        {
            throw new DenHttpClientException($"Invalid Den server URL '{baseUrl}': URI scheme must be http or https.");
        }

        var normalizedBase = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);
        return new Uri(normalizedBase, path.TrimStart('/'));
    }

    private static string EscapePathSegment(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private static void AddNonBlank(List<QueryParameter> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add(new QueryParameter(name, value));
        }
    }

    private static bool IsTransportException(Exception ex, CancellationToken cancellationToken)
    {
        if (ex is HttpRequestException)
        {
            return true;
        }

        return ex is TaskCanceledException && !cancellationToken.IsCancellationRequested;
    }

    private sealed record QueryParameter(string Name, string Value);
}

public sealed class DenHttpClientException : Exception
{
    public DenHttpClientException(string message)
        : base(message)
    {
    }

    public DenHttpClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

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

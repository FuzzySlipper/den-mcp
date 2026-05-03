using Den.Bridge.Abstractions;

namespace DenMcp.Desktop.Sidecar;

// ── Documents list handler ─────────────────────────────────────────────────

public sealed class DocumentsListHandler
    : IBridgeCommandHandler<DocumentsListRequest, DocumentsListResponse>
{
    private readonly DenHttpClient _den;
    private readonly Func<CancellationToken, Task<OperatorSettings>> _settingsProvider;

    public DocumentsListHandler(DenHttpClient den, OperatorRuntimeService runtime)
    {
        _den = den;
        _settingsProvider = runtime.GetSettingsAsync;
    }

    public async ValueTask<DocumentsListResponse?> HandleAsync(
        DocumentsListRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);

        var settings = await _settingsProvider(cancellationToken).ConfigureAwait(false);
        var baseUrl = settings.DenBaseUrl;

        var projectId = request.ProjectId;
        var isGlobal = string.Equals(projectId, "_global", StringComparison.OrdinalIgnoreCase);
        var errors = new List<string>();

        var documents = await TryAsync(
            () => _den.ListDocumentsAsync(baseUrl, isGlobal ? null : projectId, cancellationToken),
            errors,
            "Unable to load documents",
            Array.Empty<DenDocumentSummary>()).ConfigureAwait(false);

        var items = documents.Select(d => new DocumentListItem
        {
            Slug = d.Slug,
            Title = d.Title,
            DocType = d.DocType ?? "spec",
            Tags = d.Tags is not null ? d.Tags.AsReadOnly() : [],
        }).ToList();

        return new DocumentsListResponse { Documents = items };
    }

    private static async Task<T> TryAsync<T>(
        Func<Task<T>> action,
        List<string> errors,
        string context,
        T fallback)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DenHttpClientException or System.Text.Json.JsonException or HttpRequestException or TaskCanceledException)
        {
            errors.Add($"{context}: {ex.Message}");
            return fallback;
        }
    }
}

// ── Document get handler ───────────────────────────────────────────────────

public sealed class DocumentGetHandler
    : IBridgeCommandHandler<DocumentGetRequest, DocumentGetResponse>
{
    private readonly DenHttpClient _den;
    private readonly Func<CancellationToken, Task<OperatorSettings>> _settingsProvider;

    public DocumentGetHandler(DenHttpClient den, OperatorRuntimeService runtime)
    {
        _den = den;
        _settingsProvider = runtime.GetSettingsAsync;
    }

    public async ValueTask<DocumentGetResponse?> HandleAsync(
        DocumentGetRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Slug);

        var settings = await _settingsProvider(cancellationToken).ConfigureAwait(false);
        var baseUrl = settings.DenBaseUrl;

        try
        {
            var doc = await _den.GetDocumentAsync(baseUrl, request.ProjectId, request.Slug, cancellationToken).ConfigureAwait(false);
            return new DocumentGetResponse
            {
                Slug = doc.Slug,
                Title = doc.Title,
                Content = doc.Content,
                DocType = doc.DocType ?? "spec",
                Tags = doc.Tags is not null ? doc.Tags.AsReadOnly() : [],
            };
        }
        catch (DenHttpClientException)
        {
            return null;
        }
    }
}

// ── Document store handler ─────────────────────────────────────────────────

public sealed class DocumentStoreHandler
    : IBridgeCommandHandler<DocumentStoreRequest, DocumentStoreResponse>
{
    private readonly DenHttpClient _den;
    private readonly Func<CancellationToken, Task<OperatorSettings>> _settingsProvider;

    public DocumentStoreHandler(DenHttpClient den, OperatorRuntimeService runtime)
    {
        _den = den;
        _settingsProvider = runtime.GetSettingsAsync;
    }

    public async ValueTask<DocumentStoreResponse?> HandleAsync(
        DocumentStoreRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Slug);

        var settings = await _settingsProvider(cancellationToken).ConfigureAwait(false);
        var baseUrl = settings.DenBaseUrl;

        try
        {
            var apiRequest = new StoreDocumentApiRequest
            {
                Slug = request.Slug,
                Title = request.Title,
                Content = request.Content,
                DocType = request.DocType,
            };

            await _den.StoreDocumentAsync(baseUrl, request.ProjectId, apiRequest, cancellationToken).ConfigureAwait(false);

            return new DocumentStoreResponse
            {
                Slug = request.Slug,
                Title = request.Title,
                Created = true,
            };
        }
        catch (DenHttpClientException)
        {
            return new DocumentStoreResponse
            {
                Slug = request.Slug,
                Title = request.Title,
                Created = false,
            };
        }
    }
}

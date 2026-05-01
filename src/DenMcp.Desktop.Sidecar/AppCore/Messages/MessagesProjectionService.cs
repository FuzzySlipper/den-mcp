using System.Globalization;
using System.Text.Json;

namespace DenMcp.Desktop.Sidecar;

public sealed class MessagesProjectionService
{
    private const int MaxSummaryLength = 280;

    private readonly DenHttpClient _den;
    private readonly Func<CancellationToken, Task<OperatorSettings>> _settingsProvider;
    private readonly Func<DateTimeOffset> _now;

    public MessagesProjectionService(
        DenHttpClient den,
        OperatorRuntimeService runtime,
        Func<DateTimeOffset>? now = null)
        : this(den, runtime.GetSettingsAsync, now)
    {
    }

    public MessagesProjectionService(
        DenHttpClient den,
        Func<CancellationToken, Task<OperatorSettings>> settingsProvider,
        Func<DateTimeOffset>? now = null)
    {
        _den = den;
        _settingsProvider = settingsProvider;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<MessagesSnapshot> GetSnapshotAsync(
        MessagesSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);

        var generatedAt = ToIso(_now());
        var warnings = new List<string>();
        var errors = new List<string>();
        var settings = await _settingsProvider(cancellationToken).ConfigureAwait(false);
        var baseUrl = settings.DenBaseUrl;

        // Determine effective limit — clamp to [1, 100]
        var limit = Math.Clamp(request.Limit, 1, 100);

        var messages = await TryAsync(
            () => _den.ListMessagesAsync(baseUrl, request.ProjectId, request.TaskId, limit, cancellationToken),
            errors,
            "Unable to load messages",
            Array.Empty<DenMessage>()).ConfigureAwait(false);

        // Build thread root if thread_id specified
        MessagesMessageRow? threadRoot = null;
        if (request.ThreadId is { } threadId)
        {
            var threadMessages = await TryAsync(
                () => _den.ListMessagesAsync(baseUrl, request.ProjectId, null, 1, cancellationToken),
                errors,
                $"Unable to load thread root {threadId}",
                Array.Empty<DenMessage>()).ConfigureAwait(false);

            threadRoot = threadMessages
                .Where(m => m.Id == threadId)
                .Select(ToRow)
                .FirstOrDefault();
        }

        var rows = messages.Select(ToRow).ToList();

        var unreadFor = request.UnreadFor;
        var unreadCount = string.IsNullOrWhiteSpace(unreadFor)
            ? 0
            : rows.Count(m => m.IsUnread);

        return new MessagesSnapshot
        {
            SnapshotId = $"messages:{request.ProjectId}:{request.TaskId?.ToString(CultureInfo.InvariantCulture) ?? "project"}:{generatedAt}",
            ProjectId = request.ProjectId,
            TaskId = request.TaskId,
            ThreadId = request.ThreadId,
            GeneratedAt = generatedAt,
            Messages = rows,
            ThreadRoot = threadRoot,
            UnreadCount = unreadCount,
            TotalCount = rows.Count,
            Freshness = new MessagesFreshness
            {
                GeneratedAt = generatedAt,
                IsPartial = errors.Count > 0,
                Warnings = warnings,
                Errors = errors,
            },
        };
    }

    private static MessagesMessageRow ToRow(DenMessage message)
    {
        var contentSummary = BoundSummary(message.Content, MaxSummaryLength);
        var metadataType = TryGetMetadataType(message.Metadata);

        return new MessagesMessageRow
        {
            Id = message.Id,
            Sender = message.Sender,
            Content = message.Content,
            Intent = message.Intent,
            Metadata = message.Metadata,
            MetadataType = metadataType,
            TaskId = null,
            ThreadId = null,
            CreatedAt = message.CreatedAt,
            IsUnread = false,
            ContentSummary = contentSummary,
        };
    }

    private static string? TryGetMetadataType(JsonElement? metadata)
    {
        if (metadata is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String)
        {
            return type.GetString();
        }

        return null;
    }

    private static string BoundSummary(string? value, int maxChars)
    {
        var text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ReplaceLineEndings(" ");
        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }

    private static string ToIso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

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
        catch (Exception ex) when (ex is DenHttpClientException or JsonException or HttpRequestException or TaskCanceledException)
        {
            errors.Add($"{context}: {ex.Message}");
            return fallback;
        }
    }
}

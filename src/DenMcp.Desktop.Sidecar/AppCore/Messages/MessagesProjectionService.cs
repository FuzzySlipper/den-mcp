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
            () => _den.ListMessagesAsync(baseUrl, request.ProjectId, request.TaskId, limit, cancellationToken: cancellationToken),
            errors,
            "Unable to load messages",
            Array.Empty<DenMessage>()).ConfigureAwait(false);

        // Determine unread state when unreadFor is provided
        HashSet<long>? unreadIds = null;
        var unreadFor = request.UnreadFor;
        if (!string.IsNullOrWhiteSpace(unreadFor))
        {
            var unreadMessages = await TryAsync(
                () => _den.ListMessagesAsync(baseUrl, request.ProjectId, request.TaskId, limit, unreadFor, cancellationToken),
                errors,
                "Unable to load unread messages",
                Array.Empty<DenMessage>()).ConfigureAwait(false);
            unreadIds = unreadMessages.Select(m => m.Id).ToHashSet();
        }

        // Build thread root if thread_id specified
        MessagesMessageRow? threadRoot = null;
        if (request.ThreadId is { } threadId)
        {
            var rootMessage = await TryAsync<DenMessage?>(
                () => _den.GetMessageAsync(baseUrl, request.ProjectId, threadId, cancellationToken),
                errors,
                $"Unable to load thread root {threadId}",
                null).ConfigureAwait(false);

            threadRoot = rootMessage is not null ? ToRow(rootMessage, unreadIds) : null;
        }

        var rows = messages.Select(m => ToRow(m, unreadIds)).ToList();

        var unreadCount = unreadIds is not null
            ? rows.Count(m => m.IsUnread)
            : 0;

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

    private static MessagesMessageRow ToRow(DenMessage message, HashSet<long>? unreadIds = null)
    {
        var contentSummary = BoundSummary(message.Content, MaxSummaryLength);
        var metadataType = TryGetMetadataType(message.Metadata);
        var isUnread = unreadIds is not null && unreadIds.Contains(message.Id);

        return new MessagesMessageRow
        {
            Id = message.Id,
            Sender = message.Sender,
            Content = message.Content,
            Intent = message.Intent,
            Metadata = message.Metadata,
            MetadataType = metadataType,
            TaskId = message.TaskId,
            ThreadId = message.ThreadId,
            CreatedAt = message.CreatedAt,
            IsUnread = isUnread,
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

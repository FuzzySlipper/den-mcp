using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

public sealed record OperatorSettings
{
    public const string DefaultDenBaseUrl = "http://localhost:5199";
    public const string DefaultSourceDisplayName = "Den Desktop";
    public const int DefaultPollIntervalSeconds = 30;
    public const int DefaultMaxChangedFiles = 200;
    public const bool DefaultIncludeHiddenSpaces = true;
    public const bool DefaultIncludeArchivedSpaces = true;
    public const int MinPollIntervalSeconds = 5;
    public const int MaxPollIntervalSeconds = 3600;
    public const int MinChangedFiles = 25;
    public const int MaxChangedFilesLimit = 2000;

    [JsonRequired]
    [JsonPropertyName("denBaseUrl")]
    public string DenBaseUrl { get; init; } = DefaultDenBaseUrl;

    [JsonRequired]
    [JsonPropertyName("sourceInstanceId")]
    public string SourceInstanceId { get; init; } = CreateSourceInstanceId();

    [JsonPropertyName("sourceDisplayName")]
    public string? SourceDisplayName { get; init; }

    [JsonRequired]
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; init; } = DefaultPollIntervalSeconds;

    [JsonRequired]
    [JsonPropertyName("maxChangedFiles")]
    public int MaxChangedFiles { get; init; } = DefaultMaxChangedFiles;

    [JsonPropertyName("includeHiddenSpaces")]
    public bool IncludeHiddenSpaces { get; init; } = DefaultIncludeHiddenSpaces;

    [JsonPropertyName("includeArchivedSpaces")]
    public bool IncludeArchivedSpaces { get; init; } = DefaultIncludeArchivedSpaces;

    public static OperatorSettings CreateDefault(Func<string>? sourceInstanceIdFactory = null)
    {
        return new OperatorSettings
        {
            DenBaseUrl = DefaultDenBaseUrl,
            SourceInstanceId = CreateSourceInstanceId(sourceInstanceIdFactory),
            SourceDisplayName = DefaultSourceDisplayName,
            PollIntervalSeconds = DefaultPollIntervalSeconds,
            MaxChangedFiles = DefaultMaxChangedFiles,
            IncludeHiddenSpaces = DefaultIncludeHiddenSpaces,
            IncludeArchivedSpaces = DefaultIncludeArchivedSpaces,
        };
    }

    public OperatorSettings Normalized(Func<string>? sourceInstanceIdFactory = null)
    {
        var denBaseUrl = (DenBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (denBaseUrl.Length == 0)
        {
            denBaseUrl = DefaultDenBaseUrl;
        }

        var sourceInstanceId = (SourceInstanceId ?? string.Empty).Trim();
        if (sourceInstanceId.Length == 0)
        {
            sourceInstanceId = CreateSourceInstanceId(sourceInstanceIdFactory);
        }

        return this with
        {
            DenBaseUrl = denBaseUrl,
            SourceInstanceId = sourceInstanceId,
            SourceDisplayName = TrimToOption(SourceDisplayName),
            PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, MinPollIntervalSeconds, MaxPollIntervalSeconds),
            MaxChangedFiles = Math.Clamp(MaxChangedFiles, MinChangedFiles, MaxChangedFilesLimit),
        };
    }

    public static OperatorSettings FromSaveRequest(
        OperatorSettings current,
        SaveOperatorSettingsRequest request,
        Func<string>? sourceInstanceIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);

        return new OperatorSettings
        {
            DenBaseUrl = request.DenBaseUrl,
            SourceInstanceId = current.SourceInstanceId,
            SourceDisplayName = TrimToOption(request.SourceDisplayName),
            PollIntervalSeconds = request.PollIntervalSeconds ?? current.PollIntervalSeconds,
            MaxChangedFiles = request.MaxChangedFiles ?? current.MaxChangedFiles,
            IncludeHiddenSpaces = request.IncludeHiddenSpaces ?? current.IncludeHiddenSpaces,
            IncludeArchivedSpaces = request.IncludeArchivedSpaces ?? current.IncludeArchivedSpaces,
        }.Normalized(sourceInstanceIdFactory);
    }

    internal static string CreateSourceInstanceId(Func<string>? sourceInstanceIdFactory = null)
    {
        if (sourceInstanceIdFactory is not null)
        {
            var generated = sourceInstanceIdFactory().Trim();
            if (generated.Length > 0)
            {
                return generated;
            }
        }

        return "den-desktop-" + Guid.NewGuid().ToString("N");
    }

    private static string? TrimToOption(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

public sealed record SaveOperatorSettingsRequest
{
    [JsonPropertyName("denBaseUrl")]
    public string DenBaseUrl { get; init; } = OperatorSettings.DefaultDenBaseUrl;

    [JsonPropertyName("sourceDisplayName")]
    public string? SourceDisplayName { get; init; }

    [JsonPropertyName("pollIntervalSeconds")]
    public int? PollIntervalSeconds { get; init; }

    [JsonPropertyName("maxChangedFiles")]
    public int? MaxChangedFiles { get; init; }

    [JsonPropertyName("includeHiddenSpaces")]
    public bool? IncludeHiddenSpaces { get; init; }

    [JsonPropertyName("includeArchivedSpaces")]
    public bool? IncludeArchivedSpaces { get; init; }
}

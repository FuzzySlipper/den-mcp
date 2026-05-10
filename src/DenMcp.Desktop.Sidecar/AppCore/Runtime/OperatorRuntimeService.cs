using System.Text.Json.Serialization;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace DenMcp.Desktop.Sidecar;

// Bridge protocol DTOs in this file intentionally use camelCase JsonPropertyName
// values to match the TypeScript consumer interfaces (e.g. tauriApi.ts). This
// diverges from the project-wide snake_case JSON convention, which is acceptable
// because the wire-protocol schema is the source of truth and both sides use
// camelCase. See review finding R1000-1.

public sealed record OperatorStatus
{
    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "starting";

    [JsonPropertyName("denConnection")]
    public DenConnectionStatus DenConnection { get; init; } = DenConnectionStatus.Unknown("Preparing Den operator runtime.");

    [JsonPropertyName("sourceInstanceId")]
    public string SourceInstanceId { get; init; } = string.Empty;

    [JsonPropertyName("denBaseUrl")]
    public string DenBaseUrl { get; init; } = string.Empty;

    [JsonPropertyName("lastSyncAt")]
    public string? LastSyncAt { get; init; }

    [JsonPropertyName("lastPublishAt")]
    public string? LastPublishAt { get; init; }

    [JsonPropertyName("observerStatuses")]
    public IReadOnlyList<ObserverStatus> ObserverStatuses { get; init; } = [];

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<DiagnosticEntry> Diagnostics { get; init; } = [];

    [JsonPropertyName("projectCount")]
    public int ProjectCount { get; init; }

    [JsonPropertyName("workspaceCount")]
    public int WorkspaceCount { get; init; }

    [JsonPropertyName("localSnapshotCount")]
    public int LocalSnapshotCount { get; init; }

    [JsonPropertyName("localSessionSnapshotCount")]
    public int LocalSessionSnapshotCount { get; init; }

    [JsonPropertyName("spaceCount")]
    public int SpaceCount { get; init; }

    [JsonPropertyName("spaces")]
    public IReadOnlyList<OperatorSpace> Spaces { get; init; } = [];

    public static OperatorStatus Starting(OperatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new OperatorStatus
        {
            Phase = "starting",
            DenConnection = DenConnectionStatus.Unknown("Preparing Den operator runtime."),
            SourceInstanceId = settings.SourceInstanceId,
            DenBaseUrl = settings.DenBaseUrl,
            ObserverStatuses = [ObserverStatus.Stopped("git"), ObserverStatus.Stopped("session")],
        };
    }
}

public sealed record OperatorSpace
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "project";

    [JsonPropertyName("visibility")]
    public string Visibility { get; init; } = "normal";

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("rootPath")]
    public string? RootPath { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; init; }

    public static OperatorSpace FromDen(DenSpace space) => new()
    {
        Id = space.Id,
        Name = space.Name,
        Kind = space.Kind,
        Visibility = space.Visibility,
        Owner = space.Owner,
        RootPath = space.RootPath,
        Description = space.Description,
        CreatedAt = space.CreatedAt,
        UpdatedAt = space.UpdatedAt,
    };
}

public sealed record DenConnectionStatus
{
    [JsonPropertyName("state")]
    public string State { get; init; } = "unknown";

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("lastSuccessAt")]
    public string? LastSuccessAt { get; init; }

    [JsonPropertyName("lastFailureAt")]
    public string? LastFailureAt { get; init; }

    [JsonPropertyName("nextRetryAt")]
    public string? NextRetryAt { get; init; }

    public static DenConnectionStatus Unknown(string message) => new() { State = "unknown", Message = message };

    public static DenConnectionStatus Connected(DenConnectionStatus previous, string message, string at) => new()
    {
        State = "connected",
        Message = message,
        LastSuccessAt = at,
        LastFailureAt = previous.LastFailureAt,
    };

    public static DenConnectionStatus Offline(DenConnectionStatus previous, string message, string at, string nextRetryAt) => new()
    {
        State = "offline",
        Message = message,
        LastSuccessAt = previous.LastSuccessAt,
        LastFailureAt = at,
        NextRetryAt = nextRetryAt,
    };

    public static DenConnectionStatus Degraded(DenConnectionStatus previous, string message, string at, string nextRetryAt) => new()
    {
        State = "degraded",
        Message = message,
        LastSuccessAt = previous.LastSuccessAt,
        LastFailureAt = at,
        NextRetryAt = nextRetryAt,
    };

    public static DenConnectionStatus Misconfigured(DenConnectionStatus previous, string message, string at) => new()
    {
        State = "misconfigured",
        Message = message,
        LastSuccessAt = previous.LastSuccessAt,
        LastFailureAt = at,
    };
}

public sealed record ObserverStatus
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = "stopped";

    [JsonPropertyName("scopesScanned")]
    public int ScopesScanned { get; init; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; init; }

    [JsonPropertyName("lastRunAt")]
    public string? LastRunAt { get; init; }

    [JsonPropertyName("nextRunAt")]
    public string? NextRunAt { get; init; }

    public static ObserverStatus Stopped(string kind) => new() { Kind = kind, State = "stopped" };

    public static ObserverStatus Running(string kind) => new() { Kind = kind, State = "running" };

    public static ObserverStatus Ready(string kind, int scopesScanned, int warningCount, string lastRunAt, string? nextRunAt) => new()
    {
        Kind = kind,
        State = "ready",
        ScopesScanned = scopesScanned,
        WarningCount = warningCount,
        LastRunAt = lastRunAt,
        NextRunAt = nextRunAt,
    };
}

public sealed record DiagnosticEntry
{
    [JsonPropertyName("level")]
    public string Level { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("observedAt")]
    public string ObservedAt { get; init; } = string.Empty;
}

public sealed record LocalSnapshotList
{
    [JsonPropertyName("scopes")]
    public IReadOnlyList<GitScope> Scopes { get; init; } = [];

    [JsonPropertyName("snapshots")]
    public IReadOnlyList<LocalGitSnapshot> Snapshots { get; init; } = [];
}

public sealed record LocalSessionSnapshotList
{
    [JsonPropertyName("snapshots")]
    public IReadOnlyList<LocalSessionSnapshot> Snapshots { get; init; } = [];
}

public interface IOperatorRuntimeEventSink
{
    ValueTask PublishAsync(string eventName, object payload, CancellationToken cancellationToken = default);
}

public sealed class OperatorRuntimeBridgeEventSink : IOperatorRuntimeEventSink
{
    private readonly DesktopSidecarRuntimeState _sidecarState;
    private readonly object _lock = new();
    private IBridgeEventPublisher? _publisher;

    public OperatorRuntimeBridgeEventSink(DesktopSidecarRuntimeState sidecarState)
    {
        _sidecarState = sidecarState;
    }

    public IReadOnlyList<BridgeEventFrame> PublishedFrames
    {
        get
        {
            lock (_lock)
            {
                return _publishedFrames.ToArray();
            }
        }
    }

    private readonly List<BridgeEventFrame> _publishedFrames = [];

    public void SetPublisher(IBridgeEventPublisher? publisher)
    {
        lock (_lock)
        {
            _publisher = publisher;
        }
    }

    public async ValueTask PublishAsync(string eventName, object payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();

        var frame = new BridgeEventFrame
        {
            SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
            EventId = $"evt_operator_{Guid.NewGuid():N}",
            Sequence = _sidecarState.NextSequence(),
            Event = eventName,
            Payload = BridgeJson.ToElement(payload),
            Correlation = BridgeCorrelation.Empty,
            SentAt = DateTimeOffset.UtcNow,
        };

        IBridgeEventPublisher? publisher;
        lock (_lock)
        {
            _publishedFrames.Add(frame);
            publisher = _publisher;
        }

        if (publisher is not null)
        {
            await publisher.PublishAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class OperatorRuntimeService : IAsyncDisposable, IDisposable
{
    public const int MaxDiagnostics = 200;

    private readonly OperatorSettingsService _settingsService;
    private readonly DenHttpClient _den;
    private readonly GitSnapshotBuilder _git;
    private readonly PiSessionSnapshotBuilder _sessions;
    private readonly TerminalOperatorSessionService _terminalSessions;
    private readonly IOperatorRuntimeEventSink _events;
    private readonly OperatorSessionRegistry _operatorSessions;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DiagnosticEntry> _diagnostics = new();
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private long _generation;
    private OperatorSettings _settings;
    private OperatorStatus _status;
    private IReadOnlyList<DenProject> _projects = [];
    private IReadOnlyList<DenAgentWorkspace> _workspaces = [];
    private IReadOnlyList<LocalGitSnapshot> _localSnapshots = [];
    private IReadOnlyList<LocalSessionSnapshot> _localSessionSnapshots = [];
    private IReadOnlyList<DenSpace> _spaces = [];

    public OperatorRuntimeService(
        OperatorSettingsService settingsService,
        DenHttpClient den,
        GitSnapshotBuilder git,
        PiSessionSnapshotBuilder sessions,
        TerminalOperatorSessionService terminalSessions,
        IOperatorRuntimeEventSink events,
        OperatorSessionRegistry operatorSessions,
        Func<DateTimeOffset>? now = null)
    {
        _settingsService = settingsService;
        _den = den;
        _git = git;
        _sessions = sessions;
        _terminalSessions = terminalSessions;
        _events = events;
        _operatorSessions = operatorSessions;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _settings = OperatorSettings.CreateDefault().Normalized();
        _status = OperatorStatus.Starting(_settings);
    }

    public async Task StartAsync(bool runInitialRefresh = true, bool startBackgroundLoop = true, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings = _settingsService.Load();
            _status = SyncStatus(_status with
            {
                Phase = "running",
                DenConnection = DenConnectionStatus.Unknown("Preparing Den operator runtime."),
                ObserverStatuses = [ObserverStatus.Stopped("git"), ObserverStatus.Stopped("session")],
            });
            PushDiagnosticLocked("info", "runtime", "Started Den operator runtime.");
            _status = SyncStatus(_status);
            _generation++;
        }
        finally
        {
            _gate.Release();
        }

        await PublishStatusAsync(cancellationToken).ConfigureAwait(false);

        if (runInitialRefresh)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (startBackgroundLoop)
        {
            StartBackgroundLoop();
        }
    }

    public async Task StopAsync()
    {
        var cancellation = _loopCancellation;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (_loopTask is not null)
            {
                await _loopTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
            _loopCancellation = null;
            _loopTask = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _gate.Dispose();
    }

    public async Task<OperatorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperatorSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperatorSettings> SaveSettingsAsync(SaveOperatorSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        OperatorSettings settings;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings = _settingsService.Save(request);
            _settings = settings;
            _generation++;
            PushDiagnosticLocked("info", "settings", "Saved Den operator settings.");
            _status = SyncStatus(_status with { Phase = "running" });
        }
        finally
        {
            _gate.Release();
        }

        await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return settings;
    }

    public async Task<LocalSnapshotList> ListLocalSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new LocalSnapshotList
            {
                Scopes = GitSnapshotBuilder.BuildGitScopes(_projects, _workspaces),
                Snapshots = _localSnapshots,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalSessionSnapshotList> ListLocalSessionSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new LocalSessionSnapshotList { Snapshots = _localSessionSnapshots };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesktopDiffSnapshotLatestResult> GetLatestDiffSnapshotAsync(
        LatestDiffSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var effectiveRequest = string.IsNullOrWhiteSpace(request.SourceInstanceId)
            ? request with { SourceInstanceId = settings.SourceInstanceId }
            : request;
        return await _den.LatestDiffSnapshotAsync(settings.DenBaseUrl, effectiveRequest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the latest local git, diff, and session snapshots to Den.
    /// Snapshot publication intentionally uses the full refresh cycle because Den
    /// project/workspace scope discovery, local inspection, connection state, and
    /// publish status are a single consistency unit for the desktop runtime.
    /// </summary>
    public Task PublishSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        return RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        OperatorSettings settings;
        IReadOnlyList<DenProject> projects;
        IReadOnlyList<DenAgentWorkspace> workspaces;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings = _settings;
            projects = _projects;
            workspaces = _workspaces;
            _status = SyncStatus(_status with
            {
                Phase = "running",
                ObserverStatuses = [ObserverStatus.Running("git"), ObserverStatus.Running("session")],
            });
        }
        finally
        {
            _gate.Release();
        }

        // Publish an intermediate status frame while observers are "running" so the
        // UI shows activity before the full data-collection cycle completes. A final
        // status event is published after all snapshots are gathered. Consumers may
        // see multiple status events per refresh cycle; this is intentional for UI
        // responsiveness. See review finding R1000-6.
        await PublishStatusAsync(cancellationToken).ConfigureAwait(false);

        var now = NowString();
        var nextRetryAt = SecondsFromNow(settings.PollIntervalSeconds);
        if (!IsValidDenBaseUrl(settings.DenBaseUrl))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _status = SyncStatus(_status with
                {
                    DenConnection = DenConnectionStatus.Misconfigured(_status.DenConnection, $"Invalid Den server URL: {settings.DenBaseUrl}", now),
                    ObserverStatuses = [ObserverStatus.Stopped("git"), ObserverStatus.Stopped("session")],
                });
                PushDiagnosticLocked("warn", "den", "Den server URL is invalid; observers are waiting for valid settings.");
                _status = SyncStatus(_status);
            }
            finally
            {
                _gate.Release();
            }

            await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var denConnected = false;
        try
        {
            var health = await _den.HealthAsync(settings.DenBaseUrl, cancellationToken).ConfigureAwait(false);
            denConnected = true;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _status = SyncStatus(_status with
                {
                    DenConnection = DenConnectionStatus.Connected(_status.DenConnection, $"Connected to Den ({health.Status})", now),
                    LastSyncAt = now,
                });
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (DenHttpClientException ex)
        {
            await RecordDenFailureAsync("offline", ex.Message, now, nextRetryAt, cancellationToken).ConfigureAwait(false);
        }

        if (denConnected)
        {
            try
            {
                projects = await _den.ListProjectsAsync(settings.DenBaseUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (DenHttpClientException ex)
            {
                denConnected = false;
                await RecordDenFailureAsync("degraded", ex.Message, NowString(), nextRetryAt, cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<DenSpace> spaces = [];
        if (denConnected)
        {
            try
            {
                workspaces = await _den.ListAgentWorkspacesAsync(settings.DenBaseUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (DenHttpClientException ex)
            {
                denConnected = false;
                await RecordDenFailureAsync("degraded", ex.Message, NowString(), nextRetryAt, cancellationToken).ConfigureAwait(false);
            }
        }

        if (denConnected)
        {
            try
            {
                spaces = await _den.ListSpacesAsync(
                    settings.DenBaseUrl,
                    DenSpaceListOptions.FromSettings(settings),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DenHttpClientException ex)
            {
                denConnected = false;
                await RecordDenFailureAsync("degraded", ex.Message, NowString(), nextRetryAt, cancellationToken).ConfigureAwait(false);
            }
        }

        var scopes = GitSnapshotBuilder.BuildGitScopes(projects, workspaces);
        var snapshots = new List<LocalGitSnapshot>();
        var warningCount = 0;
        var publishSuccesses = 0;
        var diffPublishSuccesses = 0;
        var publishErrors = new List<string>();

        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _git.InspectScopeAsync(scope, settings, cancellationToken).ConfigureAwait(false);
            warningCount += snapshot.Request.Warnings.Count;
            if (denConnected)
            {
                try
                {
                    await _den.PublishGitSnapshotAsync(settings.DenBaseUrl, scope.ProjectId, snapshot.Request, cancellationToken).ConfigureAwait(false);
                    publishSuccesses++;
                    snapshot = snapshot with { LastPublishStatus = "published", LastPublishedAt = NowString(), LastPublishError = null };
                }
                catch (DenHttpClientException ex)
                {
                    snapshot = snapshot with { LastPublishStatus = "failed", LastPublishError = ex.Message };
                    publishErrors.Add(ex.Message);
                }
            }
            else
            {
                snapshot = snapshot with
                {
                    LastPublishStatus = "queued",
                    LastPublishError = "Den is offline; latest local snapshot is retained in memory.",
                };
            }

            snapshots.Add(snapshot);
        }

        if (denConnected)
        {
            foreach (var snapshot in snapshots)
            {
                var diffs = await _git.InspectDiffSnapshotsAsync(snapshot, cancellationToken).ConfigureAwait(false);
                foreach (var diff in diffs)
                {
                    try
                    {
                        await _den.PublishDiffSnapshotAsync(settings.DenBaseUrl, snapshot.Scope.ProjectId, diff, cancellationToken).ConfigureAwait(false);
                        diffPublishSuccesses++;
                    }
                    catch (DenHttpClientException ex)
                    {
                        publishErrors.Add(ex.Message);
                    }
                }
            }
        }

        await _terminalSessions.RediscoverAsync(cancellationToken).ConfigureAwait(false);

        var sessionResult = _sessions.ScanPiSessionSnapshots(settings, projects);
        var sessionSnapshots = sessionResult.Snapshots.Concat(_terminalSessions.BuildSnapshotListForDen()).ToList();
        var sessionPublishSuccesses = 0;
        for (var index = 0; index < sessionSnapshots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = sessionSnapshots[index];
            if (denConnected)
            {
                try
                {
                    await _den.PublishSessionSnapshotAsync(settings.DenBaseUrl, session.ProjectId, session.Request, cancellationToken).ConfigureAwait(false);
                    sessionSnapshots[index] = session with { LastPublishStatus = "published", LastPublishedAt = NowString(), LastPublishError = null };
                    sessionPublishSuccesses++;
                }
                catch (DenHttpClientException ex)
                {
                    sessionSnapshots[index] = session with { LastPublishStatus = "failed", LastPublishError = ex.Message };
                    publishErrors.Add(ex.Message);
                }
            }
            else
            {
                sessionSnapshots[index] = session with
                {
                    LastPublishStatus = "queued",
                    LastPublishError = "Den is offline; latest local session snapshot is retained in memory.",
                };
            }
        }

        // Register Pi artifact snapshots into OperatorSession registry
        // (task #1010: observe-only OperatorSession records from Pi artifacts).
        foreach (var session in sessionSnapshots)
        {
            if (string.Equals(session.Request.Backend, OperatorSessionBackend.PiArtifact, StringComparison.Ordinal)
                || string.Equals(session.Request.Kind, OperatorSessionKind.ArtifactObserver, StringComparison.Ordinal)
                || session.ArtifactRoot is not null)
            {
                _operatorSessions.RegisterFromPiSnapshot(session);
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        OperatorStatus status;
        try
        {
            _projects = projects;
            _workspaces = workspaces;
            _localSnapshots = snapshots;
            _localSessionSnapshots = sessionSnapshots;
            _spaces = spaces;
            var lastRunAt = NowString();
            _status = _status with
            {
                ObserverStatuses =
                [
                    ObserverStatus.Ready("git", scopes.Count, warningCount, lastRunAt, SecondsFromNow(settings.PollIntervalSeconds)),
                    ObserverStatus.Ready("session", sessionSnapshots.Count, sessionResult.WarningCount, lastRunAt, SecondsFromNow(settings.PollIntervalSeconds)),
                ],
                LastPublishAt = publishSuccesses > 0 || diffPublishSuccesses > 0 || sessionPublishSuccesses > 0
                    ? NowString()
                    : _status.LastPublishAt,
            };

            foreach (var error in publishErrors.Take(5))
            {
                _status = _status with { DenConnection = DenConnectionStatus.Degraded(_status.DenConnection, error, NowString(), nextRetryAt) };
                PushDiagnosticLocked("warn", "publish", error);
            }

            _status = SyncStatus(_status);
            status = _status;
        }
        finally
        {
            _gate.Release();
        }

        await _events.PublishAsync(DesktopSidecarProtocol.OperatorStatusEvent, status, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(DesktopSidecarProtocol.GitSnapshotEvent, snapshots, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(DesktopSidecarProtocol.SessionSnapshotEvent, sessionSnapshots, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddDiagnosticAsync(string level, string source, string message, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PushDiagnosticLocked(level, source, message);
            _status = SyncStatus(_status);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _diagnostics.Clear();
            _status = SyncStatus(_status);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartBackgroundLoop()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopCancellation = new CancellationTokenSource();
        var token = _loopCancellation.Token;
        _loopTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var settings = await GetSettingsAsync(token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), token).ConfigureAwait(false);
                await RefreshAsync(token).ConfigureAwait(false);
            }
        }, token);
    }

    private async Task RecordDenFailureAsync(
        string state,
        string message,
        string at,
        string nextRetryAt,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = state == "degraded"
                ? DenConnectionStatus.Degraded(_status.DenConnection, message, at, nextRetryAt)
                : DenConnectionStatus.Offline(_status.DenConnection, message, at, nextRetryAt);
            _status = SyncStatus(_status with { DenConnection = connection });
            PushDiagnosticLocked("warn", "den", message);
            _status = SyncStatus(_status);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PublishStatusAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(DesktopSidecarProtocol.OperatorStatusEvent, status, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DenSpace>> ListSpacesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _spaces;
        }
        finally
        {
            _gate.Release();
        }
    }

    private OperatorStatus SyncStatus(OperatorStatus status)
    {
        return status with
        {
            SourceInstanceId = _settings.SourceInstanceId,
            DenBaseUrl = _settings.DenBaseUrl,
            ProjectCount = _projects.Count,
            WorkspaceCount = _workspaces.Count,
            LocalSnapshotCount = _localSnapshots.Count,
            LocalSessionSnapshotCount = _localSessionSnapshots.Count,
            SpaceCount = _spaces.Count,
            Spaces = _spaces.Select(OperatorSpace.FromDen).ToArray(),
            Diagnostics = _diagnostics.ToArray(),
        };
    }

    private void PushDiagnosticLocked(string level, string source, string message)
    {
        _diagnostics.Enqueue(new DiagnosticEntry
        {
            Level = level,
            Source = source,
            Message = message,
            ObservedAt = NowString(),
        });
        while (_diagnostics.Count > MaxDiagnostics)
        {
            _diagnostics.Dequeue();
        }
    }

    private string NowString()
    {
        return _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }

    private string SecondsFromNow(int seconds)
    {
        return _now().AddSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }

    private static bool IsValidDenBaseUrl(string baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    }
}

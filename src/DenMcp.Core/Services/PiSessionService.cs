using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface IPiSessionService
{
    Task<PiSessionDetail> LaunchAsync(string projectId, PiSessionLaunchRequest request, CancellationToken cancellationToken = default);
    Task<List<PiSessionSummary>> ListAsync(PiSessionListOptions options, CancellationToken cancellationToken = default);
    Task<PiSessionDetail?> GetAsync(string projectId, string sessionId, CancellationToken cancellationToken = default);
    Task<PiSessionDetail?> TerminateAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default);
    Task<PiSessionDetail?> CleanupAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default);
    Task<PiSessionAttachInfo?> GetAttachInfoAsync(string projectId, string sessionId, PiSessionAttachRequest request, CancellationToken cancellationToken = default);
}

public sealed class PiSessionService : IPiSessionService
{
    private const string LaunchProfileKind = "pi_docker_compose";
    private readonly IPiSessionRepository _sessions;
    private readonly IPiDockerLaunchProfileRenderer _renderer;
    private readonly ITaskRepository _tasks;
    private readonly IPiSessionHost _host;
    private readonly IAgentStreamOpsService _ops;
    private readonly PiDockerLaunchProfileOptions _options;
    private readonly Func<DateTime> _utcNow;

    public PiSessionService(
        IPiSessionRepository sessions,
        IPiDockerLaunchProfileRenderer renderer,
        ITaskRepository tasks,
        IPiSessionHost host,
        IAgentStreamOpsService ops,
        PiDockerLaunchProfileOptions options,
        Func<DateTime>? utcNow = null)
    {
        _sessions = sessions;
        _renderer = renderer;
        _tasks = tasks;
        _host = host;
        _ops = ops;
        _options = options;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<PiSessionDetail> LaunchAsync(string projectId, PiSessionLaunchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("project_id is required.");
        if (request.TaskId is null)
            throw new InvalidOperationException("task_id is required for Den-owned Pi sessions.");
        var task = await _tasks.GetByIdAsync(request.TaskId.Value).ConfigureAwait(false);
        if (task is null || !string.Equals(task.ProjectId, projectId.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"task_id {request.TaskId.Value} was not found in project {projectId.Trim()}.");

        var sessionId = NormalizeIdentifier(request.SessionId) ?? PiSessionNaming.NewSessionId();
        var requestedBy = NormalizeText(request.RequestedBy) ?? "den-api";
        var profile = _renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = projectId.Trim(),
            SessionId = sessionId,
            TaskId = request.TaskId,
            WorkspaceId = request.WorkspaceId,
            Title = request.Title,
            DevDir = request.DevDir,
            PiStateDir = request.PiStateDir,
            ComposeFile = request.ComposeFile,
            Service = request.Service,
            Image = request.Image,
            PiVersion = request.PiVersion,
            NodeVersion = request.NodeVersion,
            GitConfigPath = request.GitConfigPath,
            SshDir = request.SshDir,
            GhConfigDir = request.GhConfigDir,
            CallbackPorts = request.CallbackPorts,
        });

        var launchCommand = new[] { _options.DockerExecutable }.Concat(profile.DockerComposeRunArgs).ToList();
        var now = _utcNow();
        var record = new PiSessionRecord
        {
            SessionId = sessionId,
            ProjectId = projectId.Trim(),
            TaskId = request.TaskId,
            WorkspaceId = NormalizeText(request.WorkspaceId),
            RunId = NormalizeText(request.RunId),
            Title = NormalizeText(request.Title),
            ToolProfile = NormalizeText(request.ToolProfile),
            Model = NormalizeText(request.Model),
            Provider = NormalizeText(request.Provider),
            HostId = _host.HostId,
            TmuxSessionName = PiSessionNaming.CreateTmuxSessionName(projectId, sessionId),
            ContainerName = ExtractContainerName(profile),
            State = PiSessionStates.Launching,
            LaunchProfileKind = LaunchProfileKind,
            LaunchProfileId = profile.ProfileId,
            LaunchProfileJson = JsonSerializer.Serialize(profile, PiSessionJson.Options),
            LaunchCommandJson = JsonSerializer.Serialize(launchCommand, PiSessionJson.Options),
            LaunchCommandDisplay = string.Join(" ", launchCommand),
            CreatedAt = now,
            UpdatedAt = now,
        };

        record = await _sessions.CreateAsync(record).ConfigureAwait(false);
        await AuditAsync(record, "pi_session_launch_requested", requestedBy, null, new { profile.ProfileId, request.ToolProfile, request.Model, request.Provider }, cancellationToken).ConfigureAwait(false);

        var launch = await _host.LaunchAsync(new PiSessionLaunchPlan
        {
            Record = record,
            LaunchProfile = profile,
            LaunchCommand = launchCommand,
        }, cancellationToken).ConfigureAwait(false);

        record = await _sessions.UpdateStateAsync(
            record.ProjectId,
            record.SessionId,
            launch.State,
            launch.StateReason,
            launch.StartedAt,
            launch.LastActivityAt,
            launch.State == PiSessionStates.Failed ? _utcNow() : null,
            launch.ContainerId,
            launch.ContainerName).ConfigureAwait(false);

        await AuditAsync(
            record,
            launch.State == PiSessionStates.Failed ? "pi_session_launch_failed" : "pi_session_started",
            requestedBy,
            launch.StateReason,
            new { record.HostId, record.TmuxSessionName, record.ContainerName, record.LaunchProfileId },
            cancellationToken).ConfigureAwait(false);

        return ToDetail(record, profile);
    }

    public async Task<List<PiSessionSummary>> ListAsync(PiSessionListOptions options, CancellationToken cancellationToken = default)
    {
        var records = await _sessions.ListAsync(options).ConfigureAwait(false);
        var refreshed = new List<PiSessionSummary>(records.Count);
        foreach (var record in records)
        {
            refreshed.Add(ToSummary(await RefreshIfActiveAsync(record, cancellationToken).ConfigureAwait(false)));
        }
        return refreshed;
    }

    public async Task<PiSessionDetail?> GetAsync(string projectId, string sessionId, CancellationToken cancellationToken = default)
    {
        var record = await _sessions.GetAsync(projectId, sessionId).ConfigureAwait(false);
        if (record is null)
            return null;
        record = await RefreshIfActiveAsync(record, cancellationToken).ConfigureAwait(false);
        return ToDetail(record, DeserializeProfile(record));
    }

    public async Task<PiSessionDetail?> TerminateAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _sessions.GetAsync(projectId, sessionId).ConfigureAwait(false);
        if (record is null)
            return null;
        if (record.State == PiSessionStates.Completed)
            throw new InvalidOperationException($"Pi session {sessionId} is already completed.");

        var requestedBy = NormalizeText(request.RequestedBy) ?? "den-api";
        record = await _sessions.MarkTerminationRequestedAsync(projectId, sessionId, requestedBy, request.Reason).ConfigureAwait(false);
        await AuditAsync(record, "pi_session_terminate_requested", requestedBy, request.Reason, null, cancellationToken).ConfigureAwait(false);

        var terminated = await _host.TerminateAsync(record, cancellationToken).ConfigureAwait(false);
        record = await _sessions.UpdateStateAsync(
            projectId,
            sessionId,
            terminated.State,
            terminated.StateReason,
            endedAt: terminated.EndedAt ?? (terminated.Succeeded ? _utcNow() : null)).ConfigureAwait(false);
        await AuditAsync(record,
            terminated.Succeeded ? "pi_session_terminate_completed" : "pi_session_terminate_failed",
            requestedBy,
            terminated.StateReason,
            null,
            cancellationToken).ConfigureAwait(false);

        return ToDetail(record, DeserializeProfile(record));
    }

    public async Task<PiSessionDetail?> CleanupAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _sessions.GetAsync(projectId, sessionId).ConfigureAwait(false);
        if (record is null)
            return null;
        if (PiSessionStates.IsActive(record.State))
            throw new InvalidOperationException($"Pi session {sessionId} is {record.State}; terminate it before cleanup.");

        var requestedBy = NormalizeText(request.RequestedBy) ?? "den-api";
        record = await _sessions.MarkCleanupRequestedAsync(projectId, sessionId, requestedBy, request.Reason).ConfigureAwait(false);
        await AuditAsync(record, "pi_session_cleanup_requested", requestedBy, request.Reason, null, cancellationToken).ConfigureAwait(false);

        var profile = DeserializeProfile(record);
        var cleanup = await _host.CleanupAsync(record, profile, cancellationToken).ConfigureAwait(false);
        if (!cleanup.Succeeded)
        {
            record = await _sessions.UpdateStateAsync(projectId, sessionId, cleanup.State, cleanup.StateReason).ConfigureAwait(false);
            await AuditAsync(record, "pi_session_cleanup_failed", requestedBy, cleanup.StateReason, null, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(cleanup.StateReason ?? "Pi session cleanup failed.");
        }

        record = await _sessions.MarkCleanupCompletedAsync(projectId, sessionId, cleanup.StateReason).ConfigureAwait(false);
        await AuditAsync(record, "pi_session_cleanup_completed", requestedBy, cleanup.StateReason, null, cancellationToken).ConfigureAwait(false);
        return ToDetail(record, profile);
    }

    public async Task<PiSessionAttachInfo?> GetAttachInfoAsync(string projectId, string sessionId, PiSessionAttachRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _sessions.GetAsync(projectId, sessionId).ConfigureAwait(false);
        if (record is null)
            return null;
        record = await RefreshIfActiveAsync(record, cancellationToken).ConfigureAwait(false);
        if (record.State != PiSessionStates.Running)
            throw new InvalidOperationException($"Pi session {sessionId} is {record.State}; only running sessions expose attach info.");

        var requestedBy = NormalizeText(request.RequestedBy) ?? "den-api";
        await AuditAsync(record, "pi_session_attach_info_requested", requestedBy, null, new { mode = request.Mode ?? "external_attach_info" }, cancellationToken).ConfigureAwait(false);
        return BuildAttachInfo(record);
    }

    private async Task<PiSessionRecord> RefreshIfActiveAsync(PiSessionRecord record, CancellationToken cancellationToken)
    {
        if (!PiSessionStates.IsActive(record.State))
            return record;

        var status = await _host.GetStatusAsync(record, cancellationToken).ConfigureAwait(false);
        if (status.State == record.State && status.LastActivityAt is null && status.ContainerId is null && status.ContainerName is null)
            return record;

        var updated = await _sessions.UpdateStateAsync(
            record.ProjectId,
            record.SessionId,
            status.State,
            status.StateReason,
            lastActivityAt: status.LastActivityAt,
            containerId: status.ContainerId,
            containerName: status.ContainerName).ConfigureAwait(false);

        if (status.State != record.State)
            await AuditAsync(updated, "pi_session_status_changed", "den", status.StateReason, new { from_state = record.State, to_state = status.State }, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task AuditAsync(PiSessionRecord record, string eventType, string requestedBy, string? reason, object? payload, CancellationToken cancellationToken)
    {
        var payloadJson = payload is null ? null : JsonSerializer.Serialize(payload, PiSessionJson.Options);
        await _sessions.AppendEventAsync(new PiSessionEvent
        {
            ProjectId = record.ProjectId,
            TaskId = record.TaskId,
            WorkspaceId = record.WorkspaceId,
            SessionId = record.SessionId,
            EventType = eventType,
            Payload = payloadJson,
            RequestedBy = requestedBy,
            Reason = reason,
        }).ConfigureAwait(false);

        await _ops.AppendOpsAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = eventType,
            ProjectId = record.ProjectId,
            TaskId = record.TaskId,
            Sender = requestedBy,
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = $"Pi session {record.SessionId} {eventType.Replace("pi_session_", string.Empty, StringComparison.Ordinal).Replace('_', ' ')}.",
            Metadata = JsonSerializer.SerializeToElement(new
            {
                schema = "den_pi_session",
                schema_version = 1,
                session_id = record.SessionId,
                run_id = record.RunId,
                workspace_id = record.WorkspaceId,
                host_id = record.HostId,
                tmux_session_name = record.TmuxSessionName,
                container_id = record.ContainerId,
                container_name = record.ContainerName,
                state = record.State,
                launch_profile_kind = record.LaunchProfileKind,
                launch_profile_id = record.LaunchProfileId,
                requested_by = requestedBy,
                reason
            }),
            DedupKey = $"pi-session:{record.ProjectId}:{record.SessionId}:{eventType}:{Guid.NewGuid():N}"
        }).ConfigureAwait(false);
    }

    private static PiSessionDetail ToDetail(PiSessionRecord record, PiDockerLaunchProfile? profile) => new()
    {
        Session = ToSummary(record),
        LaunchProfile = profile,
        Attach = record.State == PiSessionStates.Running ? BuildAttachInfo(record) : null,
    };

    private static PiSessionSummary ToSummary(PiSessionRecord record) => new()
    {
        SessionId = record.SessionId,
        ProjectId = record.ProjectId,
        TaskId = record.TaskId,
        WorkspaceId = record.WorkspaceId,
        RunId = record.RunId,
        Title = record.Title,
        ToolProfile = record.ToolProfile,
        Model = record.Model,
        Provider = record.Provider,
        HostId = record.HostId,
        TmuxSessionName = record.TmuxSessionName,
        ContainerId = record.ContainerId,
        ContainerName = record.ContainerName,
        State = record.State,
        StateReason = record.StateReason,
        LaunchProfileKind = record.LaunchProfileKind,
        LaunchProfileId = record.LaunchProfileId,
        LaunchCommand = DeserializeCommand(record.LaunchCommandJson),
        LaunchCommandDisplay = record.LaunchCommandDisplay,
        CreatedAt = record.CreatedAt,
        StartedAt = record.StartedAt,
        LastActivityAt = record.LastActivityAt,
        EndedAt = record.EndedAt,
        UpdatedAt = record.UpdatedAt,
        TerminationRequestedAt = record.TerminationRequestedAt,
        TerminationRequestedBy = record.TerminationRequestedBy,
        TerminationReason = record.TerminationReason,
        CleanupRequestedAt = record.CleanupRequestedAt,
        CleanupRequestedBy = record.CleanupRequestedBy,
        CleanupReason = record.CleanupReason,
        CleanupCompletedAt = record.CleanupCompletedAt,
    };

    private static PiSessionAttachInfo BuildAttachInfo(PiSessionRecord record) => new()
    {
        Mode = "external_attach_info",
        Backend = "tmux",
        TmuxSessionName = record.TmuxSessionName,
        CommandExecutable = "tmux",
        CommandArgs = ["attach-session", "-t", record.TmuxSessionName],
        Warnings = ["Attach info exposes the server host tmux session name; raw terminal output capture is owned by the follow-up output/attention task."],
    };

    private static PiDockerLaunchProfile? DeserializeProfile(PiSessionRecord record)
    {
        try
        {
            return JsonSerializer.Deserialize<PiDockerLaunchProfile>(record.LaunchProfileJson, PiSessionJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> DeserializeCommand(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, PiSessionJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ExtractContainerName(PiDockerLaunchProfile profile)
    {
        for (var i = 0; i < profile.DockerComposeRunArgs.Count - 1; i++)
        {
            if (profile.DockerComposeRunArgs[i] == "--name")
                return profile.DockerComposeRunArgs[i + 1];
        }
        return null;
    }

    private static string? NormalizeIdentifier(string? value)
    {
        var normalized = NormalizeText(value);
        if (normalized is null)
            return null;
        if (normalized.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("session_id must not contain whitespace.");
        return normalized;
    }

    private static string? NormalizeText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

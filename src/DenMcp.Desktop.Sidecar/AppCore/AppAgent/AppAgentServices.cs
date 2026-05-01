using System.Text.Json;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace DenMcp.Desktop.Sidecar;

public sealed class AppAgentToolRegistry
{
    public const string StopAgentRunDisabledReason = "Backend adapter not implemented in this foundation slice.";

    private static readonly IReadOnlyList<AppAgentToolDefinition> ToolDefinitions =
    [
        Tool("get_context", "Get Context", "read", "Build the current app-agent context packet.", "app_agent.context_requested", ["context.read"]),
        Tool("list_sessions", "List Sessions", "read", "List OperatorSession summaries and capabilities.", "app_agent.sessions_listed", ["session.read"]),
        Tool("read_activity", "Read Activity", "read", "Read bounded structured activity for an explicit session.", "app_agent.activity_read", ["session.read_activity"], requiresExplicitTarget: true),
        Tool("read_terminal", "Read Terminal", "read", "Read a bounded local terminal/activity excerpt for an explicit session; raw bytes are not persisted to Den.", "app_agent.terminal_read", ["session.read_activity"], requiresExplicitTarget: true),
        Tool("get_git_snapshot", "Get Git Snapshot", "read", "Read local git snapshot summaries for the selected scope.", "app_agent.git_snapshot_read", ["git.read"]),
        Tool("list_den_messages", "List Den Messages", "read", "Read recent Den messages for the selected project/task.", "app_agent.den_messages_listed", ["den.messages.read"], requiresExplicitTarget: true),
        Tool("list_console_commands", "List Console Commands", "read", "List safe structured console commands.", "app_agent.console_commands_listed", ["console.read"]),
        Tool("summarize_output", "Summarize Output", "read", "Produce a bounded deterministic summary of selected output.", "app_agent.output_summarized", ["summary.local"]),
        Tool("draft_den_message", "Draft Den Message", "draft", "Produce a Den task-thread/project message draft without sending it.", "app_agent.den_message_drafted", ["den.messages.draft"]),
        Tool("draft_task_update", "Draft Task Update", "draft", "Suggest task changes without applying them.", "app_agent.task_update_drafted", ["den.tasks.draft"]),
        Tool("run_command", "Run Console Command", "action", "Run an allow-listed structured console command from the command registry; no shell passthrough.", "app_agent.console_command_run", ["console.run"], requiresExplicitTarget: true),
        Tool("send_compiled_response", "Send Compiled Response", "action", "Post a compiled collaboration response to Den and optionally deliver it to a live agent session.", "app_agent.compiled_response_delivered", ["collaboration.deliver"]),
        Tool("cancel_request", "Cancel Request", "action", "Cooperatively cancel an active app-agent bridge request.", "app_agent.cancel_requested", ["app_agent.cancel"]),
        Tool("stop_agent_run", "Stop Agent Run", "action", "Stop an app-agent run when supported by the backend.", "app_agent.stop_requested", ["app_agent.stop"], enabled: false, disabledReason: StopAgentRunDisabledReason),
    ];

    public IReadOnlyList<AppAgentToolDefinition> ListTools(AppAgentSelection? selection = null)
    {
        return ToolDefinitions;
    }

    public AppAgentToolDefinition GetRequired(string name)
    {
        var tool = ToolDefinitions.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (tool is null)
        {
            throw new BridgeHandlerException(
                "app_agent.tool.not_found",
                $"App-agent tool '{name}' is not allow-listed.",
                "not_found");
        }

        if (!tool.Enabled)
        {
            throw new BridgeHandlerException(
                "app_agent.tool.disabled",
                tool.DisabledReason ?? $"App-agent tool '{name}' is disabled.",
                "unsupported_capability");
        }

        return tool;
    }

    private static AppAgentToolDefinition Tool(
        string name,
        string displayName,
        string category,
        string description,
        string auditEventType,
        IReadOnlyList<string> capabilities,
        bool enabled = true,
        string? disabledReason = null,
        bool requiresExplicitTarget = false,
        bool destructive = false,
        bool requiresConfirmation = false)
    {
        return new AppAgentToolDefinition
        {
            Name = name,
            DisplayName = displayName,
            Category = category,
            Description = description,
            Enabled = enabled,
            DisabledReason = disabledReason,
            RequiresExplicitTarget = requiresExplicitTarget,
            Destructive = destructive,
            RequiresConfirmation = requiresConfirmation,
            AuditEventType = auditEventType,
            Capabilities = capabilities,
        };
    }
}

public sealed class AppAgentAuditService
{
    private readonly DenHttpClient _den;
    private readonly OperatorRuntimeService _runtime;
    private readonly OperatorSessionRegistry _sessions;
    private readonly IOperatorRuntimeEventSink _events;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();
    private readonly List<AppendDesktopSessionEventRequest> _localEvents = [];

    public AppAgentAuditService(
        DenHttpClient den,
        OperatorRuntimeService runtime,
        OperatorSessionRegistry sessions,
        IOperatorRuntimeEventSink events,
        Func<DateTimeOffset>? now = null)
    {
        _den = den;
        _runtime = runtime;
        _sessions = sessions;
        _events = events;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<AppendDesktopSessionEventRequest> LocalEvents
    {
        get
        {
            lock (_lock)
            {
                return _localEvents.ToArray();
            }
        }
    }

    public async Task RecordRunStateAsync(
        AppAgentAuditCorrelation audit,
        string status,
        string? requestId,
        string? toolName,
        string? message,
        CancellationToken cancellationToken)
    {
        var observedAt = NowString();
        _sessions.Register(new OperatorSession
        {
            SessionId = audit.OperatorSessionId ?? audit.AgentRunId,
            GlobalRef = audit.ProjectId is null ? null : $"den-desktop://app-agent/{audit.AgentRunId}",
            Title = "Den Desktop app-agent",
            DisplayName = $"App agent {audit.AgentRunId}",
            ProjectId = audit.ProjectId,
            TaskId = audit.TaskId,
            Kind = OperatorSessionKind.Agent,
            Backend = OperatorSessionBackend.Process,
            Status = status is "complete" or "cancelled" or "failed" ? OperatorSessionStatus.Exited : OperatorSessionStatus.Running,
            CurrentCommand = toolName,
            AgentIdentity = AppAgentConstants.Actor,
            Role = "app-agent",
            Capabilities = OperatorSessionCapabilities.ObserveOnly("App-agent run record; controls are exposed through app-agent bridge commands.", canReadActivity: true),
            CreatedAt = _now().UtcDateTime,
            StartedAt = _now().UtcDateTime,
            LastObservedAt = _now().UtcDateTime,
            LastActivityAt = _now().UtcDateTime,
            SourceInstanceId = await GetSourceInstanceIdAsync(cancellationToken).ConfigureAwait(false),
            RecentActivity = [new OperatorSessionActivityItem { Kind = "app_agent", Tool = toolName, Summary = message ?? status, Timestamp = observedAt }],
            UpdatedAt = _now().UtcDateTime,
        });

        await _events.PublishAsync(DesktopSidecarProtocol.AppAgentRunStateEvent, new AppAgentRunStateEvent
        {
            AgentRunId = audit.AgentRunId,
            RequestId = requestId,
            Status = status,
            ToolName = toolName,
            Message = message,
            ObservedAt = observedAt,
        }, cancellationToken).ConfigureAwait(false);

        await RecordSessionEventAsync(audit, "app_agent.run_" + status, new
        {
            audit.TraceId,
            request_id = requestId,
            tool_name = toolName,
            message,
            raw_terminal_bytes_persisted = false,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordToolStateAsync(
        AppAgentAuditCorrelation audit,
        string toolCallId,
        string toolName,
        string status,
        string? targetSummary,
        string? startedAt,
        CancellationToken cancellationToken)
    {
        var now = NowString();
        await _events.PublishAsync(DesktopSidecarProtocol.AppAgentToolCallStateEvent, new AppAgentToolCallStateEvent
        {
            ToolCallId = toolCallId,
            AgentRunId = audit.AgentRunId,
            ToolName = toolName,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = status is "completed" or "failed" or "cancelled" ? now : null,
            Cancellable = status is "running",
            TargetSummary = targetSummary,
        }, cancellationToken).ConfigureAwait(false);

        await RecordSessionEventAsync(audit, "app_agent.tool_" + status, new
        {
            audit.TraceId,
            tool_call_id = toolCallId,
            tool_name = toolName,
            target_summary = targetSummary,
            raw_terminal_bytes_persisted = false,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordSessionEventAsync(
        AppAgentAuditCorrelation audit,
        string eventType,
        object payload,
        CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(payload, DenHttpClient.CreateJsonSerializerOptions());
        var request = new AppendDesktopSessionEventRequest
        {
            TaskId = audit.TaskId is { } taskId ? checked((int)taskId) : null,
            SourceInstanceId = await GetSourceInstanceIdAsync(cancellationToken).ConfigureAwait(false),
            SessionId = audit.OperatorSessionId ?? audit.AgentRunId,
            EventType = eventType,
            Payload = payloadJson,
            RequestedBy = AppAgentConstants.Actor,
            Reason = "app-agent bridge audit event",
            ObservedAt = _now().UtcDateTime,
        };

        lock (_lock)
        {
            _localEvents.Add(request);
        }

        if (audit.ProjectId is null)
        {
            return;
        }

        try
        {
            var settings = await _runtime.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            await _den.PublishSessionEventAsync(settings.DenBaseUrl, audit.ProjectId, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DenHttpClientException or InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            await _runtime.AddDiagnosticAsync("warn", "app-agent", $"Unable to publish app-agent session event: {ex.Message}", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<string> GetSourceInstanceIdAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtime.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return settings.SourceInstanceId;
    }

    private string NowString() => _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}

public sealed class AppAgentContextBuilder
{
    private readonly OperatorRuntimeService _runtime;
    private readonly OperatorSessionRegistry _sessions;
    private readonly IConsoleCommandRunner _commands;
    private readonly DenHttpClient _den;
    private readonly AppAgentToolRegistry _tools;
    private readonly AppAgentAuditService _audit;
    private readonly Func<DateTimeOffset> _now;

    public AppAgentContextBuilder(
        OperatorRuntimeService runtime,
        OperatorSessionRegistry sessions,
        IConsoleCommandRunner commands,
        DenHttpClient den,
        AppAgentToolRegistry tools,
        AppAgentAuditService audit,
        Func<DateTimeOffset>? now = null)
    {
        _runtime = runtime;
        _sessions = sessions;
        _commands = commands;
        _den = den;
        _tools = tools;
        _audit = audit;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<AppAgentContextPacket> BuildAsync(AppAgentBuildContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audit = CreateAudit(request.AgentRunId, request.TraceId, request.ParentRequestId, request.Selection);
        await _audit.RecordRunStateAsync(audit, "starting", request.ParentRequestId, "get_context", "Building app-agent context.", cancellationToken)
            .ConfigureAwait(false);

        var warnings = new List<string>();
        var settings = await _runtime.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await _runtime.ListLocalSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        var selectedSnapshot = SelectSnapshot(snapshots.Snapshots, request.Selection);
        var taskSummary = await BuildTaskSummaryAsync(settings, request, warnings, cancellationToken).ConfigureAwait(false);
        var terminalExcerpts = await BuildTerminalExcerptsAsync(request.TerminalExcerpts, warnings, cancellationToken).ConfigureAwait(false);
        var sessions = _sessions.List()
            .Where(session => MatchesSelection(session, request.Selection))
            .Select(ToSessionSummary)
            .ToList();
        var toolDefinitions = _tools.ListTools(request.Selection);

        var packet = new AppAgentContextPacket
        {
            Selection = request.Selection,
            TaskSummary = taskSummary,
            GitSnapshot = new AppAgentGitSnapshot { Snapshots = snapshots.Snapshots, SelectedSnapshot = selectedSnapshot },
            SessionSummaries = sessions,
            CommandSummaries = _commands.ListCommands().Select(command => new AppAgentCommandSummary
            {
                Name = command.Name,
                DisplayName = command.DisplayName,
                Description = command.Description,
                NeedsTarget = command.NeedsTarget,
            }).ToList(),
            TerminalExcerpts = terminalExcerpts,
            CollaborationState = new AppAgentCollaborationState(),
            Authority = new AppAgentAuthorityHints
            {
                AllowedTools = toolDefinitions.Where(tool => tool.Enabled).ToList(),
                DisabledTools = toolDefinitions
                    .Where(tool => !tool.Enabled)
                    .Select(tool => new AppAgentDisabledTool { Name = tool.Name, Reason = tool.DisabledReason ?? "disabled" })
                    .ToList(),
                CancelAvailable = toolDefinitions.Any(tool => tool.Name == "cancel_request" && tool.Enabled),
                StopAvailable = toolDefinitions.Any(tool => tool.Name == "stop_agent_run" && tool.Enabled),
            },
            Audit = audit,
            Warnings = warnings,
            BuiltAt = NowString(),
        };

        await _audit.RecordRunStateAsync(audit, "complete", request.ParentRequestId, "get_context", "Built app-agent context.", cancellationToken)
            .ConfigureAwait(false);
        return packet;
    }

    private async Task<AppAgentTaskSummary?> BuildTaskSummaryAsync(
        OperatorSettings settings,
        AppAgentBuildContextRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Selection.ProjectId) || request.Selection.TaskId is null)
        {
            return null;
        }

        try
        {
            var detail = await _den.GetTaskDetailAsync(settings.DenBaseUrl, request.Selection.ProjectId, request.Selection.TaskId.Value, cancellationToken)
                .ConfigureAwait(false);
            var messages = detail.RecentMessages.Count > 0
                ? detail.RecentMessages
                : await _den.ListMessagesAsync(settings.DenBaseUrl, request.Selection.ProjectId, request.Selection.TaskId, request.MessageLimit, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            return new AppAgentTaskSummary
            {
                Id = detail.Task.Id,
                ProjectId = detail.Task.ProjectId,
                Title = detail.Task.Title,
                Status = detail.Task.Status,
                Priority = detail.Task.Priority,
                Tags = detail.Task.Tags,
                Dependencies = detail.Dependencies.Select(d => new AppAgentTaskDependencySummary
                {
                    TaskId = d.TaskId,
                    Title = d.Title,
                    Status = d.Status,
                }).ToList(),
                RecentMessages = messages.Take(Math.Clamp(request.MessageLimit, 1, 50)).Select(ToMessageSummary).ToList(),
                OpenReviewFindings = detail.OpenReviewFindings.Select(f => new AppAgentReviewFindingSummary
                {
                    Id = f.Id,
                    Category = f.Category,
                    Summary = f.Summary,
                    Status = f.Status,
                }).ToList(),
                ReviewState = detail.OpenReviewFindings.Count > 0 ? "findings_open" : detail.ReviewRounds.Count > 0 ? "reviewed" : "none",
            };
        }
        catch (Exception ex) when (ex is DenHttpClientException or JsonException or TaskCanceledException or HttpRequestException)
        {
            warnings.Add($"Unable to load Den task summary: {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<AppAgentTerminalExcerpt>> BuildTerminalExcerptsAsync(
        IReadOnlyList<AppAgentTerminalExcerptRequest> requests,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var excerpts = new List<AppAgentTerminalExcerpt>();
        foreach (var request in requests.Take(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = _sessions.Get(request.SessionId);
            if (session is null || !session.Capabilities.CanReadActivity)
            {
                warnings.Add($"Unable to read activity for session '{request.SessionId}': session missing or read disabled.");
                continue;
            }

            var read = ReadActivity(session, request.AfterCursor, request.Limit);
            excerpts.Add(new AppAgentTerminalExcerpt
            {
                SessionId = request.SessionId,
                Items = read.Items,
                NextCursor = read.NextCursor,
                Truncated = read.Truncated,
                RawTerminalBytesPersisted = false,
            });
        }

        await Task.CompletedTask;
        return excerpts;
    }

    public static AppAgentAuditCorrelation CreateAudit(string? agentRunId, string? traceId, string? parentRequestId, AppAgentSelection selection)
    {
        var runId = string.IsNullOrWhiteSpace(agentRunId) ? $"app_agent_run_{Guid.NewGuid():N}" : agentRunId;
        return new AppAgentAuditCorrelation
        {
            AgentRunId = runId,
            OperatorSessionId = runId,
            TraceId = string.IsNullOrWhiteSpace(traceId) ? $"tr_{Guid.NewGuid():N}" : traceId,
            ParentRequestId = parentRequestId,
            TaskId = selection.TaskId,
            ProjectId = selection.ProjectId,
        };
    }

    public static TerminalReadActivityResponse ReadActivity(OperatorSession session, string? afterCursor, int limit)
    {
        return OperatorSessionActivityReader.Read(session, afterCursor, limit);
    }

    private static LocalGitSnapshot? SelectSnapshot(IReadOnlyList<LocalGitSnapshot> snapshots, AppAgentSelection selection)
    {
        return snapshots.FirstOrDefault(snapshot =>
                (selection.ProjectId is null || snapshot.Scope.ProjectId == selection.ProjectId)
                && (selection.TaskId is null || snapshot.Scope.TaskId == selection.TaskId)
                && (selection.WorkspaceId is null || snapshot.Scope.WorkspaceId == selection.WorkspaceId))
            ?? snapshots.FirstOrDefault();
    }

    private static bool MatchesSelection(OperatorSession session, AppAgentSelection selection)
    {
        if (!string.IsNullOrWhiteSpace(selection.SessionId))
        {
            return string.Equals(session.SessionId, selection.SessionId, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(selection.ProjectId) && !string.Equals(session.ProjectId, selection.ProjectId, StringComparison.Ordinal))
        {
            return false;
        }

        if (selection.TaskId is not null && session.TaskId != selection.TaskId)
        {
            return false;
        }

        return true;
    }

    private static AppAgentSessionSummary ToSessionSummary(OperatorSession session)
    {
        return new AppAgentSessionSummary
        {
            SessionId = session.SessionId,
            Title = session.Title,
            DisplayName = session.DisplayName,
            Kind = session.Kind,
            Backend = session.Backend,
            Status = session.Status,
            ProjectId = session.ProjectId,
            TaskId = session.TaskId,
            WorkspaceId = session.WorkspaceId,
            CurrentCommand = session.CurrentCommand,
            Capabilities = new AppAgentSessionCapabilities
            {
                CanReadActivity = session.Capabilities.CanReadActivity,
                CanAttach = session.Capabilities.CanAttach,
                CanSendInput = session.Capabilities.CanSendInput,
                CanTerminate = session.Capabilities.CanTerminate,
                CanKill = session.Capabilities.CanKill,
                Reason = session.Capabilities.Reason,
            },
            Warnings = session.Warnings,
            LastActivitySummary = session.RecentActivity.LastOrDefault()?.Summary,
        };
    }

    public static AppAgentDenMessageSummary ToMessageSummary(DenMessage message)
    {
        return new AppAgentDenMessageSummary
        {
            Id = message.Id,
            Sender = message.Sender,
            Intent = message.Intent,
            MetadataType = TryGetMetadataType(message.Metadata),
            ContentSummary = BoundSummary(message.Content, 500),
            CreatedAt = message.CreatedAt,
        };
    }

    public static string BoundSummary(string? value, int maxChars)
    {
        var text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ReplaceLineEndings(" ");
        return text.Length <= maxChars ? text : text[..maxChars] + "…";
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

    private string NowString() => _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}

public sealed class AppAgentService
{
    private readonly AppAgentContextBuilder _contextBuilder;
    private readonly AppAgentToolRegistry _tools;
    private readonly OperatorRuntimeService _runtime;
    private readonly OperatorSessionRegistry _sessions;
    private readonly IConsoleCommandRunner _commands;
    private readonly DenHttpClient _den;
    private readonly AppAgentAuditService _audit;
    private readonly CollaborationResponseDeliveryService? _deliveryService;
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeRequests = new(StringComparer.Ordinal);

    public AppAgentService(
        AppAgentContextBuilder contextBuilder,
        AppAgentToolRegistry tools,
        OperatorRuntimeService runtime,
        OperatorSessionRegistry sessions,
        IConsoleCommandRunner commands,
        DenHttpClient den,
        AppAgentAuditService audit,
        CollaborationResponseDeliveryService? deliveryService = null)
    {
        _contextBuilder = contextBuilder;
        _tools = tools;
        _runtime = runtime;
        _sessions = sessions;
        _commands = commands;
        _den = den;
        _audit = audit;
        _deliveryService = deliveryService;
    }

    public IReadOnlyList<AppAgentToolDefinition> ListTools(AppAgentSelection selection) => _tools.ListTools(selection);

    public Task<AppAgentContextPacket> BuildContextAsync(AppAgentBuildContextRequest request, CancellationToken cancellationToken)
    {
        return _contextBuilder.BuildAsync(request, cancellationToken);
    }

    public async Task<AppAgentInvokeToolResponse> InvokeToolAsync(
        string requestId,
        AppAgentInvokeToolRequest request,
        CancellationToken cancellationToken)
    {
        var tool = _tools.GetRequired(request.ToolName);
        var audit = AppAgentContextBuilder.CreateAudit(request.AgentRunId, request.TraceId, requestId, request.Selection);
        var toolCallId = $"tool_{Guid.NewGuid():N}";
        var startedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock)
        {
            _activeRequests[requestId] = linked;
        }

        try
        {
            await _audit.RecordRunStateAsync(audit, "tool_running", requestId, tool.Name, $"Running app-agent tool {tool.Name}.", linked.Token)
                .ConfigureAwait(false);
            await _audit.RecordToolStateAsync(audit, toolCallId, tool.Name, "running", TargetSummary(request.Selection), startedAt, linked.Token)
                .ConfigureAwait(false);

            var result = await ExecuteToolAsync(tool.Name, request, linked.Token).ConfigureAwait(false);

            await _audit.RecordToolStateAsync(audit, toolCallId, tool.Name, "completed", TargetSummary(request.Selection), startedAt, cancellationToken)
                .ConfigureAwait(false);
            await _audit.RecordRunStateAsync(audit, "complete", requestId, tool.Name, $"Completed app-agent tool {tool.Name}.", cancellationToken)
                .ConfigureAwait(false);
            return new AppAgentInvokeToolResponse
            {
                ToolName = tool.Name,
                ToolCallId = toolCallId,
                Status = "completed",
                Result = result,
                Audit = audit,
            };
        }
        catch (OperationCanceledException)
        {
            await _audit.RecordToolStateAsync(audit, toolCallId, tool.Name, "cancelled", TargetSummary(request.Selection), startedAt, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch
        {
            await _audit.RecordToolStateAsync(audit, toolCallId, tool.Name, "failed", TargetSummary(request.Selection), startedAt, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_lock)
            {
                _activeRequests.Remove(requestId);
            }
        }
    }

    public AppAgentCancelResponse Cancel(AppAgentCancelRequest request)
    {
        lock (_lock)
        {
            if (_activeRequests.TryGetValue(request.RequestId, out var cts))
            {
                cts.Cancel();
                return new AppAgentCancelResponse { RequestId = request.RequestId, Accepted = true, Status = "cancel_requested" };
            }
        }

        return new AppAgentCancelResponse { RequestId = request.RequestId, Accepted = false, Status = "not_found" };
    }

    private async Task<JsonElement> ExecuteToolAsync(string toolName, AppAgentInvokeToolRequest request, CancellationToken cancellationToken)
    {
        switch (toolName)
        {
            case "get_context":
                return BridgeJson.ToElement(new AppAgentBuildContextResponse
                {
                    Context = await _contextBuilder.BuildAsync(new AppAgentBuildContextRequest
                    {
                        Selection = request.Selection,
                        AgentRunId = request.AgentRunId,
                        TraceId = request.TraceId,
                        ParentRequestId = request.TraceId,
                    }, cancellationToken).ConfigureAwait(false),
                });
            case "list_sessions":
                return BridgeJson.ToElement(new TerminalListSessionsResponse
                {
                    Sessions = _sessions.List().Select(TerminalSessionSummaryProjection.FromSession).ToList(),
                    Count = _sessions.Count(),
                });
            case "read_activity":
            case "read_terminal":
                return BridgeJson.ToElement(ReadActivityTool(request.Input));
            case "get_git_snapshot":
                return BridgeJson.ToElement(await _runtime.ListLocalSnapshotsAsync(cancellationToken).ConfigureAwait(false));
            case "list_den_messages":
                return BridgeJson.ToElement(await ListDenMessagesToolAsync(request, cancellationToken).ConfigureAwait(false));
            case "list_console_commands":
                return BridgeJson.ToElement(new ConsoleCommandListResponse { Commands = _commands.ListCommands() });
            case "summarize_output":
                return BridgeJson.ToElement(SummarizeOutputTool(request.Input));
            case "draft_den_message":
                return BridgeJson.ToElement(DraftDenMessageTool(request.Input, request.Selection));
            case "draft_task_update":
                return BridgeJson.ToElement(DraftTaskUpdateTool(request.Input, request.Selection));
            case "run_command":
                return BridgeJson.ToElement(await RunCommandToolAsync(request.Input, request.Selection, cancellationToken).ConfigureAwait(false));
            case "send_compiled_response":
                return BridgeJson.ToElement(await SendCompiledResponseToolAsync(request.Input, request.Selection, cancellationToken).ConfigureAwait(false));
            case "cancel_request":
                return BridgeJson.ToElement(Cancel(ParseCancelRequest(request.Input)));
            default:
                throw new BridgeHandlerException("app_agent.tool.not_found", $"App-agent tool '{toolName}' is not implemented.", "not_found");
        }
    }

    private TerminalReadActivityResponse ReadActivityTool(JsonElement input)
    {
        var sessionId = RequiredString(input, "session_id");
        var afterCursor = OptionalString(input, "after_cursor");
        var limit = OptionalInt(input, "limit") ?? AppAgentConstants.DefaultTerminalExcerptLimit;
        var session = _sessions.Get(sessionId) ?? throw new BridgeHandlerException("app_agent.session.not_found", $"Session '{sessionId}' not found.", "not_found");
        if (!session.Capabilities.CanReadActivity)
        {
            throw new BridgeHandlerException("app_agent.read_activity.unsupported", "Session does not support reading structured activity.", "unsupported_capability");
        }

        return AppAgentContextBuilder.ReadActivity(session, afterCursor, limit);
    }

    private async Task<IReadOnlyList<AppAgentDenMessageSummary>> ListDenMessagesToolAsync(AppAgentInvokeToolRequest request, CancellationToken cancellationToken)
    {
        var projectId = OptionalString(request.Input, "project_id") ?? request.Selection.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new BridgeHandlerException("app_agent.den_messages.project_required", "list_den_messages requires a project_id.", "validation");
        }

        var taskId = OptionalLong(request.Input, "task_id") ?? request.Selection.TaskId;
        var limit = OptionalInt(request.Input, "limit") ?? AppAgentConstants.DefaultMessageLimit;
        var settings = await _runtime.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var messages = await _den.ListMessagesAsync(settings.DenBaseUrl, projectId, taskId, limit, cancellationToken: cancellationToken).ConfigureAwait(false);
        return messages.Select(AppAgentContextBuilder.ToMessageSummary).ToList();
    }

    private static object SummarizeOutputTool(JsonElement input)
    {
        var text = OptionalString(input, "text") ?? string.Empty;
        return new
        {
            summary = AppAgentContextBuilder.BoundSummary(text, 500),
            original_char_count = text.Length,
            truncated = text.Length > 500,
        };
    }

    private static object DraftDenMessageTool(JsonElement input, AppAgentSelection selection)
    {
        var content = OptionalString(input, "content") ?? string.Empty;
        return new
        {
            draft_only = true,
            project_id = OptionalString(input, "project_id") ?? selection.ProjectId,
            task_id = OptionalLong(input, "task_id") ?? selection.TaskId,
            intent = OptionalString(input, "intent") ?? "handoff",
            content,
        };
    }

    private static object DraftTaskUpdateTool(JsonElement input, AppAgentSelection selection)
    {
        return new
        {
            draft_only = true,
            project_id = OptionalString(input, "project_id") ?? selection.ProjectId,
            task_id = OptionalLong(input, "task_id") ?? selection.TaskId,
            status = OptionalString(input, "status"),
            title = OptionalString(input, "title"),
            description = OptionalString(input, "description"),
        };
    }

    private async Task<ConsoleCommandRunResponse> RunCommandToolAsync(JsonElement input, AppAgentSelection selection, CancellationToken cancellationToken)
    {
        var command = RequiredString(input, "command");
        if (_commands.ListCommands().All(candidate => candidate.Name != command))
        {
            throw new BridgeHandlerException("app_agent.console_command.not_allowed", $"Console command '{command}' is not registered.", "validation");
        }

        return await _commands.RunCommandAsync(new ConsoleCommandRunRequest
        {
            Command = command,
            ProjectId = OptionalString(input, "project_id") ?? selection.ProjectId,
            TaskId = ToNullableInt(OptionalLong(input, "task_id") ?? selection.TaskId),
            WorkspaceId = OptionalString(input, "workspace_id") ?? selection.WorkspaceId,
            SessionId = OptionalString(input, "session_id") ?? selection.SessionId,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<CollaborationSendCompiledResponseResponse> SendCompiledResponseToolAsync(
        JsonElement input,
        AppAgentSelection selection,
        CancellationToken cancellationToken)
    {
        if (_deliveryService is null)
        {
            throw new BridgeHandlerException(
                "app_agent.send_compiled_response.unavailable",
                "Collaboration response delivery service is not available.",
                "unavailable");
        }

        var sessionId = RequiredLong(input, "session_id");
        var compiledText = OptionalString(input, "compiled_text");
        var targetSessionId = OptionalString(input, "target_session_id");
        var postToDen = !input.TryGetProperty("post_to_den", out var postProp) || postProp.ValueKind != JsonValueKind.False;

        return await _deliveryService.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = sessionId,
            CompiledText = compiledText,
            TargetSessionId = targetSessionId,
            PostToDen = postToDen,
            RequestedBy = AppAgentConstants.Actor,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static AppAgentCancelRequest ParseCancelRequest(JsonElement input)
    {
        return new AppAgentCancelRequest
        {
            RequestId = RequiredString(input, "request_id"),
            Reason = OptionalString(input, "reason"),
        };
    }

    private static string TargetSummary(AppAgentSelection selection)
    {
        return string.Join(" ", new[]
        {
            selection.ProjectId is null ? null : $"project={selection.ProjectId}",
            selection.TaskId is null ? null : $"task={selection.TaskId}",
            selection.WorkspaceId is null ? null : $"workspace={selection.WorkspaceId}",
            selection.SessionId is null ? null : $"session={selection.SessionId}",
        }.Where(part => part is not null));
    }

    private static string RequiredString(JsonElement input, string property)
    {
        var value = OptionalString(input, property);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BridgeHandlerException("app_agent.input.invalid", $"Missing required input property '{property}'.", "validation");
        }

        return value;
    }

    private static string? OptionalString(JsonElement input, string property)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static int? OptionalInt(JsonElement input, string property)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(property, out var value) && value.TryGetInt32(out var number))
        {
            return number;
        }

        return null;
    }

    private static long? OptionalLong(JsonElement input, string property)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(property, out var value) && value.TryGetInt64(out var number))
        {
            return number;
        }

        return null;
    }

    private static long RequiredLong(JsonElement input, string property)
    {
        var value = OptionalLong(input, property);
        if (value is null)
        {
            throw new BridgeHandlerException("app_agent.input.invalid", $"Missing required input property '{property}'.", "validation");
        }

        return value.Value;
    }

    private static int? ToNullableInt(long? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value > int.MaxValue || value < int.MinValue)
        {
            throw new BridgeHandlerException("app_agent.input.invalid", "task_id is outside the supported range.", "validation");
        }

        return (int)value.Value;
    }
}

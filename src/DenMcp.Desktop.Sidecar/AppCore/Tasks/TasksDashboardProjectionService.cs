using System.Globalization;
using System.Text.Json;

namespace DenMcp.Desktop.Sidecar;

public sealed class TasksDashboardProjectionService
{
    private static readonly HashSet<string> PacketTypes = new(StringComparer.Ordinal)
    {
        "coder_context_packet",
        "implementation_packet",
        "validation_packet",
        "drift_check_packet",
        "review_request",
        "review_request_packet",
        "rereview_packet",
        "review_feedback",
        "review_findings_packet",
        "merge_summary",
    };

    private readonly DenHttpClient _den;
    private readonly OperatorSessionRegistry _sessions;
    private readonly Func<CancellationToken, Task<OperatorSettings>> _settingsProvider;
    private readonly Func<DateTimeOffset> _now;

    public TasksDashboardProjectionService(
        DenHttpClient den,
        OperatorRuntimeService runtime,
        OperatorSessionRegistry sessions,
        Func<DateTimeOffset>? now = null)
        : this(den, sessions, runtime.GetSettingsAsync, now)
    {
    }

    public TasksDashboardProjectionService(
        DenHttpClient den,
        OperatorSessionRegistry sessions,
        Func<CancellationToken, Task<OperatorSettings>> settingsProvider,
        Func<DateTimeOffset>? now = null)
    {
        _den = den;
        _sessions = sessions;
        _settingsProvider = settingsProvider;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<TasksDashboardSnapshot> GetSnapshotAsync(TasksDashboardSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);

        var generatedAt = ToIso(_now());
        var warnings = new List<string>
        {
            "Merge eligibility is advisory: Desktop does not yet compare reviewed heads with live branch heads in this projection.",
            "Workspace/worktree records are represented from sub-agent run summaries and local OperatorSession correlations when available.",
        };
        var errors = new List<string>();
        var settings = await _settingsProvider(cancellationToken).ConfigureAwait(false);
        var baseUrl = settings.DenBaseUrl;

        // Use tree: true when parentTaskId is null (root view) to include all tasks,
        // not just root-level. When drilling into a specific parent, use tree: false
        // to return only that parent's subtasks.
        var useTree = request.ParentTaskId is null;
        var visibleSummaries = await TryAsync(
            () => _den.ListTasksAsync(baseUrl, request.ProjectId, request.ParentTaskId, tree: useTree, cancellationToken),
            errors,
            "Unable to load task list",
            Array.Empty<DenTaskRecord>()).ConfigureAwait(false);

        var summariesById = visibleSummaries
            .Where(task => request.IncludeDone || !string.Equals(task.Status, "done", StringComparison.Ordinal))
            .ToDictionary(task => task.Id, task => task);

        if (request.ParentTaskId is { } parentId && !summariesById.ContainsKey(parentId))
        {
            var parent = await TryAsync(
                async () => await _den.GetTaskDetailAsync(baseUrl, request.ProjectId, parentId, cancellationToken).ConfigureAwait(false),
                errors,
                $"Unable to load parent task {parentId}",
                (DenTaskDetail?)null).ConfigureAwait(false);
            if (parent is not null)
            {
                summariesById[parent.Task.Id] = parent.Task;
            }
        }

        if (request.FocusedTaskId is { } focusedId && !summariesById.ContainsKey(focusedId))
        {
            var focused = await TryAsync(
                async () => await _den.GetTaskDetailAsync(baseUrl, request.ProjectId, focusedId, cancellationToken).ConfigureAwait(false),
                errors,
                $"Unable to load focused task {focusedId}",
                (DenTaskDetail?)null).ConfigureAwait(false);
            if (focused is not null && (request.IncludeDone || !string.Equals(focused.Task.Status, "done", StringComparison.Ordinal)))
            {
                summariesById[focused.Task.Id] = focused.Task;
            }
        }

        var taskIds = summariesById.Keys.OrderBy(id => id).ToArray();
        var details = new Dictionary<long, DenTaskDetail>();
        var messagesByTask = new Dictionary<long, IReadOnlyList<DenMessage>>();
        var runsByTask = new Dictionary<long, IReadOnlyList<DenSubagentRunSummary>>();
        var streamByTask = new Dictionary<long, IReadOnlyList<DenAgentStreamEntry>>();

        foreach (var taskId in taskIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = await TryAsync(
                async () => await _den.GetTaskDetailAsync(baseUrl, request.ProjectId, taskId, cancellationToken).ConfigureAwait(false),
                errors,
                $"Unable to load task detail {taskId}",
                (DenTaskDetail?)null).ConfigureAwait(false);
            if (detail is not null)
            {
                details[taskId] = detail;
                summariesById[taskId] = detail.Task;
            }

            messagesByTask[taskId] = await TryAsync(
                () => _den.ListMessagesAsync(baseUrl, request.ProjectId, taskId, 100, cancellationToken: cancellationToken),
                errors,
                $"Unable to load task-thread messages for {taskId}",
                Array.Empty<DenMessage>()).ConfigureAwait(false);

            runsByTask[taskId] = await TryAsync(
                () => _den.ListSubagentRunsAsync(baseUrl, request.ProjectId, taskId, 20, cancellationToken),
                errors,
                $"Unable to load sub-agent runs for {taskId}",
                Array.Empty<DenSubagentRunSummary>()).ConfigureAwait(false);

            streamByTask[taskId] = await TryAsync(
                () => _den.ListAgentStreamAsync(baseUrl, request.ProjectId, taskId, 30, cancellationToken),
                errors,
                $"Unable to load agent stream lifecycle for {taskId}",
                Array.Empty<DenAgentStreamEntry>()).ConfigureAwait(false);
        }

        var depths = ComputeWaveDepths(taskIds, details);
        var sessionChipsByTask = _sessions.List()
            .Where(session => string.Equals(session.ProjectId, request.ProjectId, StringComparison.Ordinal))
            .Where(session => session.TaskId is not null && taskIds.Contains(session.TaskId.Value))
            .GroupBy(session => session.TaskId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(ToSessionChip).ToList() as IReadOnlyList<TasksDashboardSessionChip>);

        var rows = taskIds
            .Select(taskId => BuildRow(taskId, summariesById[taskId], details.GetValueOrDefault(taskId), messagesByTask[taskId], runsByTask[taskId], streamByTask[taskId], sessionChipsByTask.GetValueOrDefault(taskId, []), depths))
            .OrderBy(row => row.WaveIndex)
            .ThenBy(row => row.Id)
            .ToList();

        var lanes = rows.SelectMany(row => BuildLanes(row, runsByTask[row.Id], streamByTask[row.Id])).ToList();
        var waves = BuildWaves(rows);
        var header = BuildHeader(rows, generatedAt);

        return new TasksDashboardSnapshot
        {
            SnapshotId = $"task-dashboard:{request.ProjectId}:{request.ParentTaskId?.ToString(CultureInfo.InvariantCulture) ?? "root"}:{generatedAt}",
            ProjectId = request.ProjectId,
            ParentTaskId = request.ParentTaskId,
            FocusedTaskId = request.FocusedTaskId,
            GeneratedAt = generatedAt,
            Header = header,
            Tasks = rows,
            Waves = waves,
            Lanes = lanes,
            Freshness = new TasksDashboardFreshness
            {
                GeneratedAt = generatedAt,
                IsPartial = errors.Count > 0,
                Warnings = warnings,
                Errors = errors,
            },
        };
    }

    private static TasksDashboardTaskRow BuildRow(
        long taskId,
        DenTaskRecord task,
        DenTaskDetail? detail,
        IReadOnlyList<DenMessage> messages,
        IReadOnlyList<DenSubagentRunSummary> runs,
        IReadOnlyList<DenAgentStreamEntry> stream,
        IReadOnlyList<TasksDashboardSessionChip> sessionChips,
        IReadOnlyDictionary<long, int> depths)
    {
        var packets = LatestPackets(messages);
        var review = BuildReviewSummary(detail);
        var lifecycle = BuildLifecycleSummary(stream);
        var runAggregate = BuildRunAggregate(runs);
        var stage = ComputeStage(task.Status, packets, review, lifecycle, runAggregate);
        var dependencies = (detail?.Dependencies ?? [])
            .Select(d => new TasksDashboardDependency
            {
                TaskId = d.TaskId,
                Title = d.Title,
                Status = d.Status,
                Visible = depths.ContainsKey(d.TaskId),
            })
            .ToList();

        var recentMessages = messages
            .OrderByDescending(m => ParseDate(m.CreatedAt))
            .ThenByDescending(m => m.Id)
            .Take(5)
            .Select(m => new TasksDashboardRecentMessage
            {
                Id = m.Id,
                Sender = m.Sender,
                Intent = m.Intent,
                MetadataType = TryGetMetadataType(m.Metadata),
                ContentSummary = BoundSummary(m.Content, 200),
                CreatedAt = m.CreatedAt,
            })
            .ToList();

        return new TasksDashboardTaskRow
        {
            Id = taskId,
            ProjectId = task.ProjectId,
            ParentId = task.ParentId,
            Title = task.Title,
            Status = task.Status,
            ComputedState = ComputeState(task.Status, dependencies, packets, review, lifecycle, runAggregate),
            Priority = task.Priority,
            AssignedTo = task.AssignedTo,
            Tags = task.Tags,
            Dependencies = dependencies,
            SubtaskIds = (detail?.Subtasks ?? []).Select(subtask => subtask.Id).ToList(),
            WaveIndex = depths.GetValueOrDefault(taskId, 0),
            Stage = stage,
            Packets = packets,
            Review = review,
            RunSummary = runAggregate,
            AgentLifecycle = lifecycle,
            Description = task.Description ?? string.Empty,
            MessageCount = messages.Count,
            RecentMessages = recentMessages,
            DependencyCount = task.DependencyCount,
            SubtaskCount = task.SubtaskCount,
            CreatedAt = task.CreatedAt,
            SessionChips = sessionChips,
            UpdatedAt = task.UpdatedAt,
        };
    }

    private static IReadOnlyList<TasksDashboardPacketSummary> LatestPackets(IReadOnlyList<DenMessage> messages)
    {
        return messages
            .Select(message => new { Message = message, PacketType = TryGetMetadataType(message.Metadata) })
            .Where(item => item.PacketType is not null && PacketTypes.Contains(item.PacketType))
            .GroupBy(item => item.PacketType!, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => ParseDate(item.Message.CreatedAt)).ThenByDescending(item => item.Message.Id).First())
            .Select(item => new TasksDashboardPacketSummary
            {
                PacketType = item.PacketType!,
                MessageId = item.Message.Id,
                Sender = item.Message.Sender,
                Intent = item.Message.Intent,
                CreatedAt = item.Message.CreatedAt,
                Summary = BoundSummary(item.Message.Content, 280),
                Stage = PacketStage(item.PacketType!),
            })
            .OrderBy(packet => PacketOrder(packet.PacketType))
            .ToList();
    }

    private static TasksDashboardReviewSummary BuildReviewSummary(DenTaskDetail? detail)
    {
        var rounds = detail?.ReviewRounds ?? [];
        var current = rounds.OrderByDescending(round => round.RoundNumber).ThenByDescending(round => round.Id).FirstOrDefault();
        var open = detail?.OpenReviewFindings.Count ?? 0;
        var resolved = detail?.ResolvedReviewFindings.Count ?? 0;
        var verdict = current?.Verdict;
        var state = current is null
            ? "none"
            : open > 0 || string.Equals(verdict, "changes_requested", StringComparison.Ordinal)
                ? "changes_requested"
                : string.Equals(verdict, "looks_good", StringComparison.Ordinal)
                    ? "approved"
                    : "pending";

        return new TasksDashboardReviewSummary
        {
            State = state,
            CurrentRoundId = current?.Id,
            RoundCount = rounds.Count,
            Verdict = verdict,
            Branch = current?.Branch,
            HeadCommit = current?.HeadCommit,
            OpenFindingCount = open,
            ResolvedFindingCount = resolved,
            MergeEligible = current is null ? null : string.Equals(verdict, "looks_good", StringComparison.Ordinal) && open == 0 ? null : false,
            MergeEligibilityReason = current is null
                ? "No review round is present."
                : "Live branch-head comparison is not available in this projection; reviewer must verify reviewed head before merge.",
        };
    }

    private static TasksDashboardRunAggregate BuildRunAggregate(IReadOnlyList<DenSubagentRunSummary> runs)
    {
        var summaries = runs.Select(ToRunSummary).OrderByDescending(run => ParseDate(run.EndedAt) ?? ParseDate(run.StartedAt)).ToList();
        var totalTokens = summaries.Sum(run => run.Usage?.TotalTokens ?? 0);
        var hasTokens = summaries.Any(run => run.Usage?.TotalTokens is not null);
        var totalCost = summaries.Sum(run => run.Usage?.TotalCost ?? 0);
        var hasCost = summaries.Any(run => run.Usage?.TotalCost is not null);
        return new TasksDashboardRunAggregate
        {
            RunCount = summaries.Count,
            ActiveRunCount = summaries.Count(run => IsActiveRunState(run.State)),
            LatestRun = summaries.FirstOrDefault(),
            TotalTokens = hasTokens ? totalTokens : null,
            TotalCost = hasCost ? totalCost : null,
            Currency = summaries.Select(run => run.Usage?.Currency).FirstOrDefault(currency => !string.IsNullOrWhiteSpace(currency)),
        };
    }

    private static TasksDashboardRunSummary ToRunSummary(DenSubagentRunSummary run)
    {
        return new TasksDashboardRunSummary
        {
            RunId = run.RunId,
            Role = run.Role,
            State = run.State,
            Model = run.Model,
            Purpose = run.Purpose,
            Branch = run.Branch,
            HeadCommit = run.FinalHeadCommit ?? run.HeadCommit,
            StartedAt = ToIso(run.StartedAt),
            EndedAt = ToIso(run.EndedAt),
            DurationMs = run.DurationMs,
            Usage = run.UsageSummary is null ? null : new TasksDashboardUsageSummary
            {
                InputTokens = run.UsageSummary.InputTokens,
                OutputTokens = run.UsageSummary.OutputTokens,
                TotalTokens = run.UsageSummary.TotalTokens,
                TotalCost = run.UsageSummary.TotalCost,
                Currency = run.UsageSummary.Currency,
                Source = run.UsageSummary.Source,
            },
            LifecycleEvents = run.OperatorEvents
                .Where(evt => !string.Equals(evt.Visibility, "debug", StringComparison.Ordinal))
                .Select(evt => new TasksDashboardRunLifecycleEvent
                {
                    EventName = evt.EventName,
                    OccurredAt = ToIso(evt.OccurredAt),
                    Source = evt.Source,
                })
                .ToList(),
        };
    }

    private static TasksDashboardLifecycleSummary BuildLifecycleSummary(IReadOnlyList<DenAgentStreamEntry> stream)
    {
        var lifecycle = stream
            .Where(entry => IsLifecycleEvent(entry.EventType))
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .ToList();
        var latest = lifecycle.FirstOrDefault();
        return new TasksDashboardLifecycleSummary
        {
            State = latest is null ? "none" : LifecycleState(latest.EventType),
            LatestEvent = latest is null ? null : ToAgentStreamEvent(latest),
            EventCount = lifecycle.Count,
        };
    }

    private static TasksDashboardAgentStreamEvent ToAgentStreamEvent(DenAgentStreamEntry entry)
    {
        return new TasksDashboardAgentStreamEvent
        {
            EntryId = entry.Id,
            EventType = entry.EventType,
            Sender = entry.Sender,
            RecipientAgent = entry.RecipientAgent,
            CreatedAt = ToIso(entry.CreatedAt),
            Summary = BoundSummary(entry.Body, 240),
        };
    }

    private static IReadOnlyDictionary<long, int> ComputeWaveDepths(IReadOnlyList<long> taskIds, IReadOnlyDictionary<long, DenTaskDetail> details)
    {
        var visible = taskIds.ToHashSet();
        var memo = new Dictionary<long, int>();
        int Visit(long id, HashSet<long> stack)
        {
            if (memo.TryGetValue(id, out var cached))
            {
                return cached;
            }

            if (!stack.Add(id))
            {
                memo[id] = 0;
                return 0;
            }

            var deps = details.GetValueOrDefault(id)?.Dependencies.Where(dep => visible.Contains(dep.TaskId)).Select(dep => dep.TaskId) ?? [];
            var depth = deps.Any() ? deps.Max(dep => Visit(dep, stack)) + 1 : 0;
            stack.Remove(id);
            memo[id] = depth;
            return depth;
        }

        foreach (var id in taskIds)
        {
            Visit(id, []);
        }

        return memo;
    }

    private static IReadOnlyList<TasksDashboardWave> BuildWaves(IReadOnlyList<TasksDashboardTaskRow> rows)
    {
        return rows
            .GroupBy(row => row.WaveIndex)
            .OrderBy(group => group.Key)
            .Select(group => new TasksDashboardWave
            {
                Index = group.Key,
                Label = $"wave {group.Key + 1}",
                State = WaveState(group.Select(row => row.ComputedState).ToList()),
                TaskIds = group.Select(row => row.Id).OrderBy(id => id).ToList(),
                Summary = string.Join(", ", group.GroupBy(row => row.ComputedState).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")),
            })
            .ToList();
    }

    private static IReadOnlyList<TasksDashboardLane> BuildLanes(
        TasksDashboardTaskRow row,
        IReadOnlyList<DenSubagentRunSummary> runs,
        IReadOnlyList<DenAgentStreamEntry> stream)
    {
        var lanes = new List<TasksDashboardLane>();
        foreach (var run in runs.OrderByDescending(run => run.EndedAt ?? run.StartedAt).Take(3))
        {
            var summary = ToRunSummary(run);
            lanes.Add(new TasksDashboardLane
            {
                LaneKey = $"run:{run.RunId}",
                TaskId = row.Id,
                Label = $"{run.Role ?? "sub-agent"} · #{row.Id}",
                Role = run.Role,
                State = run.State,
                Branch = run.Branch,
                WorktreePath = run.WorktreePath,
                LatestRun = summary,
                LatestAgentEvent = row.AgentLifecycle.LatestEvent,
                SessionChips = row.SessionChips,
            });
        }

        if (lanes.Count == 0 && (row.SessionChips.Count > 0 || row.AgentLifecycle.LatestEvent is not null))
        {
            lanes.Add(new TasksDashboardLane
            {
                LaneKey = $"task:{row.Id}",
                TaskId = row.Id,
                Label = $"task · #{row.Id}",
                State = row.AgentLifecycle.State,
                LatestAgentEvent = row.AgentLifecycle.LatestEvent,
                SessionChips = row.SessionChips,
            });
        }

        return lanes;
    }

    private static TasksDashboardHeader BuildHeader(IReadOnlyList<TasksDashboardTaskRow> rows, string generatedAt)
    {
        var done = rows.Count(row => string.Equals(row.Status, "done", StringComparison.Ordinal));
        var active = rows.Count(row => row.ComputedState is "running" or "needs_attention");
        var review = rows.Count(row => row.ComputedState == "review");
        var blocked = rows.Count(row => row.ComputedState == "blocked");
        var totalTokens = rows.Sum(row => row.RunSummary.TotalTokens ?? 0);
        var hasTokens = rows.Any(row => row.RunSummary.TotalTokens is not null);
        var totalCost = rows.Sum(row => row.RunSummary.TotalCost ?? 0);
        var hasCost = rows.Any(row => row.RunSummary.TotalCost is not null);
        return new TasksDashboardHeader
        {
            State = blocked > 0 ? "blocked" : active > 0 ? "running" : review > 0 ? "review" : done == rows.Count && rows.Count > 0 ? "done" : "queued",
            TaskCount = rows.Count,
            DoneCount = done,
            ActiveCount = active,
            ReviewCount = review,
            BlockedCount = blocked,
            CompletionPercent = rows.Count == 0 ? 0 : (int)Math.Round(done * 100.0 / rows.Count),
            TotalTokens = hasTokens ? totalTokens : null,
            TotalCost = hasCost ? totalCost : null,
            Currency = rows.Select(row => row.RunSummary.Currency).FirstOrDefault(currency => !string.IsNullOrWhiteSpace(currency)),
            LastUpdatedAt = rows.Select(row => row.UpdatedAt).Where(value => !string.IsNullOrWhiteSpace(value)).DefaultIfEmpty(generatedAt).Max(StringComparer.Ordinal),
        };
    }

    private static TasksDashboardSessionChip ToSessionChip(OperatorSession session)
    {
        return new TasksDashboardSessionChip
        {
            SessionId = session.SessionId,
            DisplayName = session.DisplayName ?? session.Title,
            Kind = session.Kind,
            Status = session.Status,
            Role = session.Role,
            SourceInstanceId = session.SourceInstanceId,
            Capabilities = new TasksDashboardSessionCapabilities
            {
                CanAttach = session.Capabilities.CanAttach,
                CanReadActivity = session.Capabilities.CanReadActivity,
                CanFocus = session.Capabilities.CanFocus,
                CanOpenExternalAttach = session.Capabilities.CanOpenExternalAttach,
                Reason = session.Capabilities.Reason,
            },
            LastActivitySummary = session.RecentActivity.LastOrDefault()?.Summary,
        };
    }

    private static string ComputeStage(
        string status,
        IReadOnlyList<TasksDashboardPacketSummary> packets,
        TasksDashboardReviewSummary review,
        TasksDashboardLifecycleSummary lifecycle,
        TasksDashboardRunAggregate runAggregate)
    {
        if (packets.Any(packet => packet.PacketType == "merge_summary") || status == "done") return "merged_or_done";
        if (review.State == "approved") return "review_approved";
        if (review.State == "changes_requested") return "changes_requested";
        if (review.State == "pending") return "review_requested";
        if (packets.Any(packet => packet.PacketType == "drift_check_packet")) return "drift_check_complete";
        if (packets.Any(packet => packet.PacketType == "validation_packet")) return "validation_complete";
        if (packets.Any(packet => packet.PacketType == "implementation_packet")) return "implementation_posted";
        if (runAggregate.ActiveRunCount > 0 || lifecycle.State == "running") return "agent_running";
        if (packets.Any(packet => packet.PacketType == "coder_context_packet")) return "context_prepared";
        return status == "planned" ? "planned" : status;
    }

    private static string ComputeState(
        string status,
        IReadOnlyList<TasksDashboardDependency> dependencies,
        IReadOnlyList<TasksDashboardPacketSummary> packets,
        TasksDashboardReviewSummary review,
        TasksDashboardLifecycleSummary lifecycle,
        TasksDashboardRunAggregate runAggregate)
    {
        if (status == "done" || packets.Any(packet => packet.PacketType == "merge_summary")) return "done";
        if (status == "blocked" || dependencies.Any(dep => dep.Status is not ("done" or null))) return "blocked";
        if (review.State is "changes_requested" || packets.Any(packet => packet.PacketType == "drift_check_packet" && packet.Summary.Contains("blocking", StringComparison.OrdinalIgnoreCase))) return "needs_attention";
        if (status == "review" || review.State is "pending" or "approved") return "review";
        if (runAggregate.ActiveRunCount > 0 || lifecycle.State == "running" || status == "in_progress") return "running";
        return "queued";
    }

    private static string PacketStage(string packetType) => packetType switch
    {
        "coder_context_packet" => "context_prepared",
        "implementation_packet" => "implementation_posted",
        "validation_packet" => "validation_completed",
        "drift_check_packet" => "drift_check_completed",
        "review_request" or "review_request_packet" or "rereview_packet" => "review_requested",
        "review_feedback" or "review_findings_packet" => "review_feedback",
        "merge_summary" => "merged",
        _ => "packet_seen",
    };

    private static int PacketOrder(string packetType) => packetType switch
    {
        "coder_context_packet" => 10,
        "implementation_packet" => 20,
        "validation_packet" => 30,
        "drift_check_packet" => 40,
        "review_request" or "review_request_packet" or "rereview_packet" => 50,
        "review_feedback" or "review_findings_packet" => 60,
        "merge_summary" => 70,
        _ => 100,
    };

    private static bool IsActiveRunState(string state) => state is "running" or "retrying" or "aborting" or "rerun_requested";

    private static bool IsLifecycleEvent(string eventType) =>
        eventType.StartsWith("subagent_", StringComparison.Ordinal) ||
        eventType is "coder_started" or "coder_completed" or "reviewer_started" or "reviewer_completed" or "agent_error" or "merge_handoff" or "review_approved" or "changes_requested" or "review_requested";

    private static string LifecycleState(string eventType)
    {
        if (eventType.Contains("error", StringComparison.Ordinal) || eventType.Contains("failed", StringComparison.Ordinal) || eventType.Contains("timeout", StringComparison.Ordinal)) return "needs_attention";
        if (eventType.Contains("completed", StringComparison.Ordinal) || eventType.Contains("approved", StringComparison.Ordinal)) return "completed";
        if (eventType.Contains("started", StringComparison.Ordinal) || eventType.Contains("heartbeat", StringComparison.Ordinal) || eventType.Contains("work", StringComparison.Ordinal)) return "running";
        return "observed";
    }

    private static string WaveState(IReadOnlyList<string> states)
    {
        if (states.Contains("needs_attention")) return "needs_attention";
        if (states.Contains("blocked")) return "blocked";
        if (states.Contains("running")) return "running";
        if (states.Contains("review")) return "review";
        if (states.All(state => state == "done")) return "done";
        return "queued";
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

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static DateTime? ParseDate(DateTime? value) => value;

    private static string? ToIso(DateTime? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string ToIso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string BoundSummary(string? value, int maxChars)
    {
        var text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ReplaceLineEndings(" ");
        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }

    private static async Task<T> TryAsync<T>(Func<Task<T>> action, List<string> errors, string context, T fallback)
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

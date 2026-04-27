using System.Diagnostics;
using System.Text.Json;
using DenMcp.Core.Models;
using Terminal.Gui;
using Terminal.Gui.Trees;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Cli.Commands;

public static class DashboardCommand
{
    public static async Task<int> Run(DenApiClient client, CommandRouter router)
    {
        Application.Init();

        try
        {
            var dashboard = new DashboardView(client, router.Project, router.HasFlag("legacy-dispatches"));
            Application.Top.Add(dashboard);
            _ = dashboard.StartPolling();
            Application.Run();
        }
        finally
        {
            Application.Shutdown();
        }

        return await Task.FromResult(0);
    }
}

internal sealed class TaskNode
{
    public TaskSummary Summary { get; }
    public List<TaskNode> Children { get; set; } = [];
    public bool ChildrenLoaded { get; set; }

    public TaskNode(TaskSummary summary) => Summary = summary;

    public override string ToString()
    {
        var prio = Summary.Priority switch { 1 => "!!", 2 => "! ", _ => "  " };
        var icon = Summary.Status switch
        {
            TaskStatus.Planned => "[ ]",
            TaskStatus.InProgress => "[>]",
            TaskStatus.Review => "[?]",
            TaskStatus.Blocked => "[!]",
            TaskStatus.Done => "[x]",
            TaskStatus.Cancelled => "[-]",
            _ => "[ ]"
        };
        var subs = Summary.SubtaskCount > 0 && !ChildrenLoaded
            ? $" (+{Summary.SubtaskCount})"
            : "";
        return $"{prio}{icon} #{Summary.Id,-4} {Summary.Title}{subs}";
    }
}

internal sealed class TaskTreeBuilder : ITreeBuilder<TaskNode>
{
    public bool SupportsCanExpand => true;

    public bool CanExpand(TaskNode node) => node.Summary.SubtaskCount > 0;

    public IEnumerable<TaskNode> GetChildren(TaskNode node) => node.Children;
}

internal enum DashboardPaneMode
{
    Tasks,
    Documents,
    Dispatches
}

internal sealed class DashboardView : Toplevel
{
    private readonly DenApiClient _client;
    private readonly string? _initialProject;
    private readonly bool _showLegacyDispatches;
    private string? _currentProject;
    private readonly FrameView _projectFrame;
    private readonly ListView _projectList;
    private readonly FrameView _agentFrame;
    private readonly ListView _agentList;
    private readonly FrameView _taskFrame;
    private readonly TreeView<TaskNode> _taskTree;
    private readonly FrameView _messageFrame;
    private readonly ListView _messageList;
    private readonly StatusBar _statusBar;
    private readonly StatusItem _changeStatusStatusItem;
    private readonly StatusItem _approveDispatchStatusItem;
    private readonly StatusItem _rejectDispatchStatusItem;
    private readonly CancellationTokenSource _cts = new();

    // Documents mode
    private readonly ListView _dispatchList;
    private readonly ListView _docList;
    private DashboardPaneMode _paneMode;
    private List<DocumentSummary> _documents = [];
    private List<DispatchEntry> _dispatches = [];

    private List<Project> _projects = [];
    private List<TaskNode> _rootNodes = [];
    private List<Message> _messages = [];
    private List<AgentSession> _activeAgents = [];

    // Preserve selected task ID across refreshes
    private int? _selectedTaskId;
    private int? _selectedDispatchId;

    // Status filter: null = show all
    private string? _statusFilter;
    private MessageIntent? _messageIntentFilter;

    // Sort mode: "priority" | "id" | "status" | "title"
    private string _sortMode = "priority";

    // Guards against concurrent refreshes
    private bool _refreshing;
    private bool _updatingProjectList;

    public DashboardView(DenApiClient client, string? initialProject, bool showLegacyDispatches)
    {
        _client = client;
        _initialProject = initialProject;
        _showLegacyDispatches = showLegacyDispatches;

        // --- Layout: bottom-up preference ---
        // Bottom: Projects (left sidebar) + Tasks/Docs (main area) — 55%
        // Middle: Agents — 10%
        // Top: Messages — remaining

        // Message feed (top area)
        _messageList = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false
        };
        _messageList.OpenSelectedItem += OnMessageActivated;

        _messageFrame = new FrameView("Messages")
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(35)
        };
        _messageFrame.Add(_messageList);

        // Active agents (middle band)
        _agentList = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false
        };

        _agentFrame = new FrameView("Agents")
        {
            X = 0, Y = Pos.Percent(35),
            Width = Dim.Fill(),
            Height = Dim.Percent(10)
        };
        _agentFrame.Add(_agentList);

        // Project list (bottom-left sidebar)
        _projectList = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false
        };
        _projectList.SelectedItemChanged += OnProjectSelected;

        _projectFrame = new FrameView("Projects")
        {
            X = 0, Y = Pos.Percent(45),
            Width = 22,
            Height = Dim.Fill(1)
        };
        _projectFrame.Add(_projectList);

        // Task tree (bottom-right main area)
        _taskTree = new TreeView<TaskNode>
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            TreeBuilder = new TaskTreeBuilder(),
            MultiSelect = false
        };
        _taskTree.ObjectActivated += OnTaskActivated;
        _taskTree.SelectionChanged += OnTaskSelectionChanged;

        // Document list (alternate view for task frame)
        _docList = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
            Visible = false
        };
        _docList.OpenSelectedItem += OnDocumentActivated;

        _dispatchList = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
            Visible = false
        };
        _dispatchList.OpenSelectedItem += OnDispatchActivated;
        _dispatchList.SelectedItemChanged += OnDispatchSelected;

        _taskFrame = new FrameView("Tasks")
        {
            X = 22, Y = Pos.Percent(45),
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };
        _taskFrame.Add(_taskTree);
        _taskFrame.Add(_docList);
        _taskFrame.Add(_dispatchList);

        // Status bar
        _changeStatusStatusItem = new(Key.S, "~S~ Status", OnChangeStatus, () => _paneMode == DashboardPaneMode.Tasks);
        _approveDispatchStatusItem = new(Key.A, "~A~ Approve", ApproveSelectedDispatch,
            () => _showLegacyDispatches && _paneMode == DashboardPaneMode.Dispatches);
        _rejectDispatchStatusItem = new(Key.X, "~X~ Reject", RejectSelectedDispatch,
            () => _showLegacyDispatches && _paneMode == DashboardPaneMode.Dispatches);

        var statusItems = new List<StatusItem>
        {
            new(Key.Q | Key.CtrlMask, "~^Q~ Quit", () => Application.RequestStop()),
            new(Key.R, "~R~ Refresh", () => _ = RefreshData()),
            _changeStatusStatusItem,
            new(Key.N, "~N~ Next", OnShowNext),
            new(Key.F, "~F~ Filter", OnOpenFilter),
            new(Key.O, "~O~ Sort", OnChangeSort),
            new(Key.V, "~V~ View", CyclePaneMode)
        };
        if (_showLegacyDispatches)
        {
            statusItems.Add(_approveDispatchStatusItem);
            statusItems.Add(_rejectDispatchStatusItem);
        }
        statusItems.Add(new(Key.Tab, "~Tab~ Switch", CycleFocus));
        _statusBar = new StatusBar(statusItems.ToArray());

        Add(_messageFrame, _agentFrame, _projectFrame, _taskFrame, _statusBar);
        ApplyPaneMode();
    }

    private void CyclePaneMode()
    {
        _paneMode = _paneMode switch
        {
            DashboardPaneMode.Tasks => DashboardPaneMode.Documents,
            DashboardPaneMode.Documents when _showLegacyDispatches => DashboardPaneMode.Dispatches,
            _ => DashboardPaneMode.Tasks
        };

        ApplyPaneMode();

        if (_currentProject is null)
            return;

        switch (_paneMode)
        {
            case DashboardPaneMode.Documents:
                _ = RefreshDocuments();
                break;
            case DashboardPaneMode.Dispatches when _showLegacyDispatches:
                _ = RefreshDispatches();
                break;
            case DashboardPaneMode.Dispatches:
                _paneMode = DashboardPaneMode.Tasks;
                _ = RefreshProjectData();
                break;
            default:
                _ = RefreshProjectData();
                break;
        }
    }

    private void ApplyPaneMode()
    {
        _taskTree.Visible = _paneMode == DashboardPaneMode.Tasks;
        _docList.Visible = _paneMode == DashboardPaneMode.Documents;
        _dispatchList.Visible = _showLegacyDispatches && _paneMode == DashboardPaneMode.Dispatches;
        _statusBar.SetNeedsDisplay();
    }

    public async Task StartPolling()
    {
        await RefreshData();

        // Poll every 5 seconds
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, _cts.Token);
                    Application.MainLoop.Invoke(() => _ = RefreshData());
                }
                catch (OperationCanceledException) { break; }
                catch { /* ignore polling errors */ }
            }
        });
    }

    private async Task RefreshData()
    {
        try
        {
            _projects = await _client.ListProjectsAsync();
            var projectNames = _projects.Select(p => p.Id).ToList();

            _updatingProjectList = true;
            _projectList.SetSource(projectNames);

            if (_currentProject is null && _initialProject is not null)
                _currentProject = _initialProject;

            if (_currentProject is null && _projects.Count > 0)
                _currentProject = _projects[0].Id;

            if (_currentProject is not null)
            {
                var idx = _projects.FindIndex(p => p.Id == _currentProject);
                if (idx >= 0)
                    _projectList.SelectedItem = idx;
            }
            _updatingProjectList = false;

            if (_currentProject is not null)
            {
                switch (_paneMode)
                {
                    case DashboardPaneMode.Documents:
                        await RefreshDocuments();
                        break;
                    case DashboardPaneMode.Dispatches when _showLegacyDispatches:
                        await RefreshDispatches();
                        break;
                    case DashboardPaneMode.Dispatches:
                        _paneMode = DashboardPaneMode.Tasks;
                        await RefreshProjectData();
                        break;
                    default:
                        await RefreshProjectData();
                        break;
                }
            }
        }
        catch
        {
            _updatingProjectList = false;
            _taskFrame.Title = "Tasks (error connecting)";
        }
    }

    private async Task RefreshProjectData()
    {
        if (_currentProject is null) return;
        if (_refreshing) return;
        _refreshing = true;

        try
        {
            // Save selected task ID before refresh
            var selectedObj = _taskTree.SelectedObject;
            if (selectedObj is not null)
                _selectedTaskId = selectedObj.Summary.Id;

            var isGlobal = _currentProject == "_global";
            var tasks = await _client.ListTasksAsync(_currentProject, status: _statusFilter);

            _rootNodes = [];
            foreach (var t in tasks)
            {
                var node = new TaskNode(t);
                if (t.SubtaskCount > 0)
                {
                    // Pre-fetch subtasks so tree can expand
                    await LoadChildrenAsync(node);
                }
                _rootNodes.Add(node);
            }

            SortNodes(_rootNodes);

            // Rebuild tree preserving expansion state
            var expandedIds = CollectExpandedIds();
            _taskTree.ClearObjects();
            foreach (var node in _rootNodes)
                _taskTree.AddObject(node);

            // Re-expand previously expanded nodes
            RestoreExpansion(_rootNodes, expandedIds);

            // Restore selected task by ID
            if (_selectedTaskId is not null)
            {
                var target = FindNodeById(_rootNodes, _selectedTaskId.Value);
                if (target is not null)
                {
                    _taskTree.SelectedObject = target;
                    _taskTree.EnsureVisible(target);
                }
            }

            var filterLabel = _statusFilter is not null ? $" [{_statusFilter}]" : "";
            var sortLabel = _sortMode != "priority" ? $" ↕{_sortMode}" : "";
            _taskFrame.Title = $"Tasks — {_currentProject} ({CountAll(_rootNodes)}){filterLabel}{sortLabel}";
            await RefreshMessagesAndAgents(isGlobal);
        }
        catch { /* ignore refresh errors */ }
        finally { _refreshing = false; }
    }

    private async Task RefreshDocuments()
    {
        if (_currentProject is null) return;

        try
        {
            var isGlobal = _currentProject == "_global";
            _documents = await _client.ListDocumentsAsync(isGlobal ? null : _currentProject);

            // Sort by most recently updated
            _documents = _documents.OrderByDescending(d => d.UpdatedAt).ToList();

            var docLines = _documents.Select(d =>
            {
                var time = FormatShortTime(d.UpdatedAt);
                var projectTag = isGlobal ? $"[{Truncate(d.ProjectId, 12)}] " : "";
                var tags = d.Tags is { Count: > 0 } ? $" [{string.Join(",", d.Tags)}]" : "";
                return $"[{time}] {projectTag}{d.DocType}: {d.Title}{tags}";
            }).ToList();

            _docList.SetSource(docLines.Count > 0 ? docLines : new List<string> { " (no documents)" });
            _taskFrame.Title = $"Documents — {_currentProject} ({_documents.Count})";
            await RefreshMessagesAndAgents(isGlobal);
        }
        catch { /* ignore refresh errors */ }
    }

    private async Task RefreshDispatches()
    {
        if (_currentProject is null) return;
        if (_refreshing) return;
        _refreshing = true;

        try
        {
            var isGlobal = _currentProject == "_global";
            if (_dispatchList.SelectedItem >= 0 && _dispatchList.SelectedItem < _dispatches.Count)
                _selectedDispatchId = _dispatches[_dispatchList.SelectedItem].Id;

            _dispatches = await _client.ListDispatchesAsync(isGlobal ? null : _currentProject, status: "pending");

            var dispatchLines = _dispatches
                .Select(dispatch => FormatDispatchLine(dispatch, isGlobal))
                .ToList();
            _dispatchList.SetSource(dispatchLines.Count > 0
                ? dispatchLines
                : new List<string> { " (legacy/debug only; no pending dispatches)" });
            _taskFrame.Title = $"Legacy Dispatches — {_currentProject} (pending {_dispatches.Count})";

            if (_selectedDispatchId is not null)
            {
                var index = _dispatches.FindIndex(dispatch => dispatch.Id == _selectedDispatchId.Value);
                if (index >= 0)
                    _dispatchList.SelectedItem = index;
                else if (_dispatches.Count > 0)
                    _dispatchList.SelectedItem = 0;
            }
            else if (_dispatches.Count > 0)
            {
                _dispatchList.SelectedItem = 0;
            }

            await RefreshMessagesAndAgents(isGlobal);
        }
        catch { /* ignore refresh errors */ }
        finally { _refreshing = false; }
    }

    private async Task RefreshMessagesAndAgents(bool isGlobal)
    {
        if (_currentProject is null) return;

        _messages = await _client.GetMessagesAsync(_currentProject, limit: 15, intent: _messageIntentFilter);
        var msgLines = _messages.Select(m =>
        {
            var time = FormatShortTime(m.CreatedAt);
            var projectTag = isGlobal ? $"[{Truncate(m.ProjectId, 12)}] " : "";
            var intent = Fmt.MessageIntentLabel(m.Intent ?? MessageIntent.General);
            return $"[{time}] {projectTag}[{intent}] {m.Sender}: {Truncate(m.Content.ReplaceLineEndings(" "), 50)}";
        }).ToList();
        _messageList.SetSource(msgLines);
        var intentLabel = _messageIntentFilter is not null ? $" [{_messageIntentFilter.Value.ToDbValue()}]" : "";
        _messageFrame.Title = $"Messages — {_currentProject}{intentLabel} ({_messages.Count})";

        await CleanupDeadSessionsAsync();

        _activeAgents = await _client.ListActiveAgentsAsync(isGlobal ? null : _currentProject);
        var agentLines = _activeAgents.Select(a =>
        {
            var ago = FormatShortTime(a.LastHeartbeat);
            return isGlobal
                ? $" {Truncate(a.Agent, 9)}@{Truncate(a.ProjectId, 6)} ({ago})"
                : $" {Truncate(a.Agent, 14)} ({ago})";
        }).ToList();
        _agentList.SetSource(agentLines.Count > 0 ? agentLines : new List<string> { " (none)" });
        _agentFrame.Title = $"Agents ({_activeAgents.Count})";
    }

    private void OnDocumentActivated(ListViewItemEventArgs args)
    {
        if (args.Item < 0 || args.Item >= _documents.Count) return;
        var doc = _documents[args.Item];
        ShowDocumentDetail(doc);
    }

    private void OnDispatchActivated(ListViewItemEventArgs args)
    {
        if (args.Item < 0 || args.Item >= _dispatches.Count) return;
        ShowDispatchDetail(_dispatches[args.Item]);
    }

    private void OnDispatchSelected(ListViewItemEventArgs args)
    {
        if (args.Item >= 0 && args.Item < _dispatches.Count)
            _selectedDispatchId = _dispatches[args.Item].Id;
    }

    private async void ShowDocumentDetail(DocumentSummary summary)
    {
        Document? doc = null;
        try
        {
            doc = await _client.GetDocumentAsync(summary.ProjectId, summary.Slug);
        }
        catch { /* fall back to summary */ }

        var dlg = new Dialog($"Document: {summary.Title}", 78, 30);

        var lines = new List<string>
        {
            summary.Title,
            new string('─', Math.Min(summary.Title.Length, 74)),
            "",
            $"  Slug:     {summary.Slug}",
            $"  Type:     {summary.DocType}",
            $"  Project:  {summary.ProjectId}",
            $"  Updated:  {summary.UpdatedAt:yyyy-MM-dd HH:mm}"
        };

        if (summary.Tags is { Count: > 0 })
            lines.Add($"  Tags:     {string.Join(", ", summary.Tags)}");

        if (doc?.Content is not null)
        {
            lines.Add("");
            lines.Add("  ──────────────────────────────────────");
            foreach (var line in doc.Content.Split('\n'))
                lines.Add($"  {line}");
        }

        var content = string.Join("\n", lines);
        var textView = new TextView
        {
            X = 1, Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = true,
            Text = content
        };
        dlg.Add(textView);

        var close = new Button("Close") { IsDefault = true };
        close.Clicked += () => Application.RequestStop();
        dlg.AddButton(close);

        Application.Run(dlg);
    }

    private async void ShowDispatchDetail(DispatchEntry summary)
    {
        DispatchEntry dispatch = summary;
        try
        {
            dispatch = await _client.GetDispatchAsync(summary.Id);
        }
        catch { /* fall back to list summary */ }

        var dlg = new Dialog($"Legacy Dispatch #{dispatch.Id}", 84, 28);

        var lines = new List<string>
        {
            $"  Status:   {dispatch.Status.ToDbValue()}",
            $"  Project:  {dispatch.ProjectId}",
            $"  Agent:    {dispatch.TargetAgent}",
            $"  Trigger:  {dispatch.TriggerType.ToDbValue()} #{dispatch.TriggerId}",
            $"  Task:     {(dispatch.TaskId is not null ? $"#{dispatch.TaskId}" : "(none)")}",
            $"  Created:  {dispatch.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC",
            $"  Expires:  {dispatch.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC"
        };

        if (dispatch.DecidedAt is not null)
            lines.Add($"  Decided:  {dispatch.DecidedAt:yyyy-MM-dd HH:mm:ss} UTC by {dispatch.DecidedBy ?? "unknown"}");
        if (dispatch.CompletedAt is not null)
            lines.Add($"  Complete: {dispatch.CompletedAt:yyyy-MM-dd HH:mm:ss} UTC by {dispatch.CompletedBy ?? "unknown"}");

        if (!string.IsNullOrWhiteSpace(dispatch.Summary))
        {
            lines.Add("");
            lines.Add("  Summary:");
            foreach (var line in dispatch.Summary.Split('\n'))
                lines.Add($"    {line}");
        }

        lines.Add("");
        lines.Add("  Prompt:");
        if (!string.IsNullOrWhiteSpace(dispatch.ContextPrompt))
        {
            foreach (var line in dispatch.ContextPrompt.Split('\n'))
                lines.Add($"    {line}");
        }
        else
        {
            lines.Add("    (none stored)");
        }

        var textView = new TextView
        {
            X = 1, Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = true,
            Text = string.Join("\n", lines)
        };
        dlg.Add(textView);

        if (dispatch.Status == DispatchStatus.Pending)
        {
            var approve = new Button("Approve");
            approve.Clicked += () =>
            {
                Application.RequestStop();
                ApproveDispatch(dispatch);
            };
            dlg.AddButton(approve);

            var reject = new Button("Reject");
            reject.Clicked += () =>
            {
                Application.RequestStop();
                RejectDispatch(dispatch);
            };
            dlg.AddButton(reject);
        }

        var close = new Button("Close") { IsDefault = true };
        close.Clicked += () => Application.RequestStop();
        dlg.AddButton(close);

        Application.Run(dlg);
    }

    private static TaskNode? FindNodeById(List<TaskNode> nodes, int id)
    {
        foreach (var node in nodes)
        {
            if (node.Summary.Id == id) return node;
            if (node.ChildrenLoaded)
            {
                var found = FindNodeById(node.Children, id);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private async Task LoadChildrenAsync(TaskNode parent)
    {
        if (parent.ChildrenLoaded) return;
        try
        {
            var subtasks = await _client.ListTasksAsync(
                parent.Summary.ProjectId, status: _statusFilter, parentId: parent.Summary.Id);
            parent.Children = [];
            foreach (var st in subtasks)
            {
                var child = new TaskNode(st);
                if (st.SubtaskCount > 0)
                    await LoadChildrenAsync(child);
                parent.Children.Add(child);
            }
            parent.ChildrenLoaded = true;
        }
        catch { /* ignore */ }
    }

    private HashSet<int> CollectExpandedIds()
    {
        var ids = new HashSet<int>();
        CollectExpanded(_rootNodes, ids);
        return ids;
    }

    private void CollectExpanded(List<TaskNode> nodes, HashSet<int> ids)
    {
        foreach (var node in nodes)
        {
            if (_taskTree.IsExpanded(node))
                ids.Add(node.Summary.Id);
            if (node.ChildrenLoaded)
                CollectExpanded(node.Children, ids);
        }
    }

    private void RestoreExpansion(List<TaskNode> nodes, HashSet<int> expandedIds)
    {
        foreach (var node in nodes)
        {
            if (expandedIds.Contains(node.Summary.Id) && node.Summary.SubtaskCount > 0)
                _taskTree.Expand(node);
            if (node.ChildrenLoaded)
                RestoreExpansion(node.Children, expandedIds);
        }
    }

    private static int CountAll(List<TaskNode> nodes)
    {
        var count = nodes.Count;
        foreach (var n in nodes)
            if (n.ChildrenLoaded) count += CountAll(n.Children);
        return count;
    }

    private void OnProjectSelected(ListViewItemEventArgs args)
    {
        if (_updatingProjectList) return;
        if (args.Item >= 0 && args.Item < _projects.Count)
        {
            _currentProject = _projects[args.Item].Id;
            _selectedTaskId = null; // Reset selection on project change
            _selectedDispatchId = null;
            switch (_paneMode)
            {
                case DashboardPaneMode.Documents:
                    _ = RefreshDocuments();
                    break;
                case DashboardPaneMode.Dispatches when _showLegacyDispatches:
                    _ = RefreshDispatches();
                    break;
                case DashboardPaneMode.Dispatches:
                    _paneMode = DashboardPaneMode.Tasks;
                    _ = RefreshProjectData();
                    break;
                default:
                    _ = RefreshProjectData();
                    break;
            }
        }
    }

    private void OnTaskActivated(ObjectActivatedEventArgs<TaskNode> args)
    {
        var node = args.ActivatedObject;
        if (node is null || _currentProject is null) return;

        ShowTaskDetail(node);
    }

    private void OnTaskSelectionChanged(object? sender, SelectionChangedEventArgs<TaskNode> args)
    {
        // Track selection for preservation across refreshes
        if (args.NewValue is not null)
            _selectedTaskId = args.NewValue.Summary.Id;
    }

    private async void ShowTaskDetail(TaskNode node)
    {
        var task = node.Summary;

        // Fetch full detail from API
        TaskDetail? detail = null;
        try
        {
            detail = await _client.GetTaskAsync(_currentProject!, task.Id);
        }
        catch { /* fall back to summary-only view */ }

        var dlg = new Dialog($"Task #{task.Id}", 78, 30);

        var lines = new List<string>
        {
            task.Title,
            new string('─', Math.Min(task.Title.Length, 74)),
            "",
            $"  Status:   {task.Status.ToDbValue(),-14} Priority: P{task.Priority}",
            $"  Assigned: {task.AssignedTo ?? "(none)"}"
        };

        if (task.Tags is { Count: > 0 })
            lines.Add($"  Tags:     {string.Join(", ", task.Tags)}");

        if (detail?.ReviewWorkflow.ReviewRoundCount > 0 && detail.ReviewWorkflow.CurrentRound is not null)
        {
            var currentRound = detail.ReviewWorkflow.CurrentRound;
            var verdict = detail.ReviewWorkflow.CurrentVerdict?.ToDbValue() ?? "pending";
            var diff = $"{currentRound.PreferredDiff.BaseRef}...{currentRound.PreferredDiff.HeadRef}";
            var delta = currentRound.DeltaDiff?.BaseCommit is not null
                ? $"{currentRound.DeltaDiff.BaseCommit}..{currentRound.HeadCommit}"
                : "(initial review)";

            lines.Add("");
            lines.Add("  Review Workflow:");
            lines.Add($"    Current:   R{currentRound.RoundNumber} [{verdict}] {Truncate(currentRound.Branch, 42)}");
            lines.Add($"    Diff:      {Truncate(diff, 55)}");
            lines.Add($"    Delta:     {Truncate(delta, 55)}");
            lines.Add($"    Findings:  {detail.ReviewWorkflow.UnresolvedFindingCount} open / {detail.ReviewWorkflow.ResolvedFindingCount} resolved");
        }

        if (detail?.OpenReviewFindings is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("  Open Review Findings:");
            foreach (var finding in detail.OpenReviewFindings.Take(6))
            {
                lines.Add($"    {Truncate(FormatFindingHeader(finding), 70)}");

                var metaParts = new List<string>();
                if (finding.FileReferences is { Count: > 0 })
                    metaParts.Add($"files {JoinPreview(finding.FileReferences, 2)}");
                if (finding.TestCommands is { Count: > 0 })
                    metaParts.Add($"tests {JoinPreview(finding.TestCommands, 1)}");
                if (!string.IsNullOrWhiteSpace(finding.StatusNotes))
                    metaParts.Add(CollapseWhitespace(finding.StatusNotes));
                else if (!string.IsNullOrWhiteSpace(finding.ResponseNotes))
                    metaParts.Add(CollapseWhitespace(finding.ResponseNotes));

                if (metaParts.Count > 0)
                    lines.Add($"      {Truncate(string.Join(" | ", metaParts), 68)}");
            }

            if (detail.OpenReviewFindings.Count > 6)
                lines.Add($"    ... {detail.OpenReviewFindings.Count - 6} more");
        }

        if (detail?.ReviewWorkflow.Timeline is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("  Review Timeline:");
            foreach (var entry in detail.ReviewWorkflow.Timeline.Take(6))
            {
                var verdict = entry.Verdict?.ToDbValue() ?? "pending";
                lines.Add($"    R{entry.ReviewRoundNumber} [{verdict}] {Truncate(entry.Branch, 28)} ({FormatShortTime(entry.RequestedAt)})");
                lines.Add($"      {Truncate(FormatTimelineSummary(entry), 66)}");
            }
        }

        // Dependencies
        if (detail?.Dependencies is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("  Dependencies:");
            foreach (var dep in detail.Dependencies)
                lines.Add($"    #{dep.TaskId,-4} [{dep.Status.ToDbValue()}] {Truncate(dep.Title, 55)}");
        }

        // Subtasks
        if (detail?.Subtasks is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("  Subtasks:");
            foreach (var sub in detail.Subtasks)
            {
                var icon = sub.Status switch
                {
                    TaskStatus.Done => "x",
                    TaskStatus.InProgress => ">",
                    TaskStatus.Blocked => "!",
                    _ => " "
                };
                lines.Add($"    [{icon}] #{sub.Id,-4} {Truncate(sub.Title, 55)}");
            }
        }

        // Description
        var desc = detail?.Task.Description ?? null;
        if (desc is not null)
        {
            lines.Add("");
            lines.Add("  ──────────────────────────────────────");
            foreach (var line in desc.Split('\n'))
                lines.Add($"  {line}");
        }

        // Recent messages
        if (detail?.RecentMessages is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("  Recent Messages:");
            foreach (var msg in detail.RecentMessages)
            {
                var ago = FormatShortTime(msg.CreatedAt);
                var intent = Fmt.MessageIntentLabel(msg.Intent ?? MessageIntent.General);
                lines.Add($"    [{ago}] [{intent}] {msg.Sender}: {Truncate(msg.Content.ReplaceLineEndings(" "), 50)}");
            }
        }

        var content = string.Join("\n", lines);
        var textView = new TextView
        {
            X = 1, Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = true,
            Text = content
        };
        dlg.Add(textView);

        var statusBtn = new Button("Set Status");
        statusBtn.Clicked += () =>
        {
            Application.RequestStop();
            ChangeStatusForTask(node);
        };
        dlg.AddButton(statusBtn);

        var close = new Button("Close") { IsDefault = true };
        close.Clicked += () => Application.RequestStop();
        dlg.AddButton(close);

        Application.Run(dlg);
    }

    private void ChangeStatusForTask(TaskNode node)
    {
        var task = node.Summary;
        var statuses = new[] { "planned", "in_progress", "review", "blocked", "done", "cancelled" };

        // Pre-select current status
        var currentIdx = Array.IndexOf(statuses, task.Status.ToDbValue());
        if (currentIdx < 0) currentIdx = 0;

        var dlg = new Dialog($"Set Status — #{task.Id}", 35, 12);
        var list = new ListView(statuses)
        {
            X = 1, Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2),
            SelectedItem = currentIdx
        };
        dlg.Add(list);

        list.OpenSelectedItem += async (e) =>
        {
            var newStatus = statuses[e.Item];
            try
            {
                await _client.UpdateTaskAsync(_currentProject!, task.Id, "user",
                    new Dictionary<string, object?> { ["status"] = newStatus });
                Application.RequestStop();
                await RefreshProjectData();
            }
            catch { Application.RequestStop(); }
        };

        var cancel = new Button("Cancel");
        cancel.Clicked += () => Application.RequestStop();
        dlg.AddButton(cancel);
        Application.Run(dlg);
    }

    private void OnChangeStatus()
    {
        if (_paneMode != DashboardPaneMode.Tasks)
            return;
        var selected = _taskTree.SelectedObject;
        if (selected is null || _currentProject is null) return;
        ChangeStatusForTask(selected);
    }

    private async void OnShowNext()
    {
        if (_currentProject is null) return;
        try
        {
            var next = await _client.NextTaskAsync(_currentProject);
            var msg = next is not null
                ? $"Next: #{next.Id} [P{next.Priority}] {next.Title}"
                : "No unblocked tasks available.";
            MessageBox.Query("Next Task", msg, "OK");
        }
        catch (Exception ex)
        {
            MessageBox.Query("Error", ex.Message, "OK");
        }
    }

    private void ApproveSelectedDispatch()
    {
        if (!_showLegacyDispatches || _paneMode != DashboardPaneMode.Dispatches) return;
        if (_dispatchList.SelectedItem < 0 || _dispatchList.SelectedItem >= _dispatches.Count) return;
        ApproveDispatch(_dispatches[_dispatchList.SelectedItem]);
    }

    private void RejectSelectedDispatch()
    {
        if (!_showLegacyDispatches || _paneMode != DashboardPaneMode.Dispatches) return;
        if (_dispatchList.SelectedItem < 0 || _dispatchList.SelectedItem >= _dispatches.Count) return;
        RejectDispatch(_dispatches[_dispatchList.SelectedItem]);
    }

    private async void ApproveDispatch(DispatchEntry dispatch)
    {
        var confirmed = MessageBox.Query(
            "Approve Legacy Dispatch",
            $"Approve legacy/debug dispatch #{dispatch.Id} for {dispatch.TargetAgent} on {dispatch.ProjectId}?",
            "Approve",
            "Cancel");
        if (confirmed != 0) return;

        try
        {
            await _client.ApproveDispatchAsync(dispatch.Id, "user");
            await RefreshDispatches();
        }
        catch (Exception ex)
        {
            MessageBox.Query("Error", ex.Message, "OK");
        }
    }

    private async void RejectDispatch(DispatchEntry dispatch)
    {
        var confirmed = MessageBox.Query(
            "Reject Legacy Dispatch",
            $"Reject legacy/debug dispatch #{dispatch.Id} for {dispatch.TargetAgent} on {dispatch.ProjectId}?",
            "Reject",
            "Cancel");
        if (confirmed != 0) return;

        try
        {
            await _client.RejectDispatchAsync(dispatch.Id, "user");
            await RefreshDispatches();
        }
        catch (Exception ex)
        {
            MessageBox.Query("Error", ex.Message, "OK");
        }
    }

    private void OnChangeSort()
    {
        if (_paneMode != DashboardPaneMode.Tasks)
            return;

        var options = new[] { "priority", "id", "status", "title" };
        var currentIdx = Array.IndexOf(options, _sortMode);
        if (currentIdx < 0) currentIdx = 0;

        var dlg = new Dialog("Sort Tasks By", 28, 10);
        var list = new ListView(options)
        {
            X = 1, Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2),
            SelectedItem = currentIdx
        };
        dlg.Add(list);

        list.OpenSelectedItem += (e) =>
        {
            _sortMode = options[e.Item];
            Application.RequestStop();
            _ = RefreshProjectData();
        };

        var cancel = new Button("Cancel");
        cancel.Clicked += () => Application.RequestStop();
        dlg.AddButton(cancel);
        Application.Run(dlg);
    }

    private void SortNodes(List<TaskNode> nodes)
    {
        var sorted = _sortMode switch
        {
            "id"     => nodes.OrderBy(n => n.Summary.Id).ToList(),
            "status" => nodes.OrderBy(n => n.Summary.Status).ThenBy(n => n.Summary.Priority).ThenBy(n => n.Summary.Id).ToList(),
            "title"  => nodes.OrderBy(n => n.Summary.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            _        => nodes.OrderBy(n => n.Summary.Priority).ThenBy(n => n.Summary.Id).ToList(),
        };
        nodes.Clear();
        nodes.AddRange(sorted);
        foreach (var node in nodes.Where(n => n.ChildrenLoaded))
            SortNodes(node.Children);
    }

    private void OnOpenFilter()
    {
        if (_messageFrame.HasFocus)
        {
            OnFilterMessageIntent();
            return;
        }

        OnFilterStatus();
    }

    private void OnFilterStatus()
    {
        if (_paneMode != DashboardPaneMode.Tasks)
            return;

        var options = new[] { "(all)", "planned", "in_progress", "review", "blocked", "done", "cancelled" };

        var currentIdx = 0;
        if (_statusFilter is not null)
        {
            var idx = Array.IndexOf(options, _statusFilter);
            if (idx >= 0) currentIdx = idx;
        }

        var dlg = new Dialog("Filter by Status", 30, 13);
        var list = new ListView(options)
        {
            X = 1, Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2),
            SelectedItem = currentIdx
        };
        dlg.Add(list);

        list.OpenSelectedItem += (e) =>
        {
            _statusFilter = e.Item == 0 ? null : options[e.Item];
            Application.RequestStop();
            _ = RefreshProjectData();
        };

        var cancel = new Button("Cancel");
        cancel.Clicked += () => Application.RequestStop();
        dlg.AddButton(cancel);
        Application.Run(dlg);
    }

    private void OnFilterMessageIntent()
    {
        var options = new[]
        {
            "(all)",
            "handoff",
            "review_feedback",
            "review_approval",
            "review_request",
            "question",
            "task_blocked",
            "task_ready",
            "status_update",
            "note",
            "answer",
            "general"
        };

        var currentIdx = 0;
        if (_messageIntentFilter is not null)
        {
            var idx = Array.IndexOf(options, _messageIntentFilter.Value.ToDbValue());
            if (idx >= 0) currentIdx = idx;
        }

        var dlg = new Dialog("Filter Messages by Intent", 34, 16);
        var list = new ListView(options)
        {
            X = 1, Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2),
            SelectedItem = currentIdx
        };
        dlg.Add(list);

        list.OpenSelectedItem += e =>
        {
            _messageIntentFilter = e.Item == 0 ? null : EnumExtensions.ParseMessageIntent(options[e.Item]);
            Application.RequestStop();
            _ = RefreshMessagesAndAgents(string.Equals(_currentProject, "_global", StringComparison.Ordinal));
        };

        var cancel = new Button("Cancel");
        cancel.Clicked += () => Application.RequestStop();
        dlg.AddButton(cancel);
        Application.Run(dlg);
    }

    public override bool ProcessKey(KeyEvent kb)
    {
        // Intercept global shortcuts before child views (TreeView/ListView) consume them
        if (!kb.IsAlt && !kb.IsCtrl)
        {
            switch (char.ToLower((char)kb.Key))
            {
                case 'r': _ = RefreshData(); return true;
                case 's': OnChangeStatus(); return true;
                case 'n': OnShowNext(); return true;
                case 'f': OnOpenFilter(); return true;
                case 'o': OnChangeSort(); return true;
                case 'v': CyclePaneMode(); return true;
                case 'a' when _showLegacyDispatches: ApproveSelectedDispatch(); return true;
                case 'x' when _showLegacyDispatches: RejectSelectedDispatch(); return true;
            }
        }
        return base.ProcessKey(kb);
    }

    private void OnMessageActivated(ListViewItemEventArgs args)
    {
        if (args.Item < 0 || args.Item >= _messages.Count) return;
        ShowMessageDetail(_messages[args.Item]);
    }

    private async void ShowMessageDetail(Message msg)
    {
        var lines = new List<string>
        {
            $"  From:    {msg.Sender}",
            $"  Intent:  {(msg.Intent ?? MessageIntent.General).ToDbValue()}",
            $"  Project: {msg.ProjectId}",
            $"  Time:    {msg.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC",
        };

        if (msg.TaskId is not null)
            lines.Add($"  Task:    #{msg.TaskId}");

        if (msg.ThreadId is not null)
            lines.Add($"  Reply to: #{msg.ThreadId}");

        // Try to load thread replies if this is a root message
        DenMcp.Core.Models.Thread? thread = null;
        try
        {
            thread = await _client.GetThreadAsync(msg.ProjectId, msg.Id);
        }
        catch { /* no thread or not a root message */ }

        lines.Add("");
        lines.Add("  ──────────────────────────────────────");
        foreach (var line in msg.Content.Split('\n'))
            lines.Add($"  {line}");

        if (thread?.Replies is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("  Replies:");
            foreach (var reply in thread.Replies)
            {
                var ago = FormatShortTime(reply.CreatedAt);
                var intent = Fmt.MessageIntentLabel(reply.Intent ?? MessageIntent.General);
                lines.Add($"    [{ago}] [{intent}] {reply.Sender}:");
                foreach (var rline in reply.Content.Split('\n'))
                    lines.Add($"      {rline}");
            }
        }

        var dlg = new Dialog($"Message from {msg.Sender} [{(msg.Intent ?? MessageIntent.General).ToDbValue()}]", 78, 24);
        var textView = new TextView
        {
            X = 1, Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = true,
            Text = string.Join("\n", lines)
        };
        dlg.Add(textView);

        var close = new Button("Close") { IsDefault = true };
        close.Clicked += () => Application.RequestStop();
        dlg.AddButton(close);

        Application.Run(dlg);
    }

    private void CycleFocus()
    {
        if (_messageFrame.HasFocus)
            _agentFrame.SetFocus();
        else if (_agentFrame.HasFocus)
            _projectFrame.SetFocus();
        else if (_projectFrame.HasFocus)
            _taskFrame.SetFocus();
        else
            _messageFrame.SetFocus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string FormatShortTime(DateTime dt)
    {
        var diff = DateTime.UtcNow - dt;
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h";
        return $"{(int)diff.TotalDays}d";
    }

    private static string FormatFindingHeader(ReviewFinding finding) =>
        $"{finding.FindingKey} [{finding.Category.ToDbValue()}/{finding.Status.ToDbValue()}] {finding.Summary}";

    private static string FormatTimelineSummary(ReviewTimelineEntry entry)
    {
        var parts = new List<string> { $"{entry.TotalFindingCount} findings" };
        if (entry.OpenFindingCount > 0)
            parts.Add($"{entry.OpenFindingCount} open");
        if (entry.AddressedFindingCount > 0)
            parts.Add($"{entry.AddressedFindingCount} addressed");
        if (entry.ResolvedFindingCount > 0)
            parts.Add($"{entry.ResolvedFindingCount} resolved");
        if (entry.CommitsSinceLastReview is not null)
            parts.Add($"{entry.CommitsSinceLastReview.Value} new commits");
        return string.Join(" | ", parts);
    }

    private static string JoinPreview(IReadOnlyList<string> values, int maxItems)
    {
        var visible = values.Take(maxItems).ToList();
        var suffix = values.Count > maxItems ? $" +{values.Count - maxItems} more" : "";
        return string.Join(", ", visible) + suffix;
    }

    private static string CollapseWhitespace(string value) =>
        value.ReplaceLineEndings(" ").Trim();

    private static string FormatDispatchLine(DispatchEntry dispatch, bool isGlobal)
    {
        var age = FormatShortTime(dispatch.CreatedAt);
        var projectTag = isGlobal ? $"[{Truncate(dispatch.ProjectId, 12)}] " : "";
        var taskTag = dispatch.TaskId is not null ? $" #{dispatch.TaskId}" : "";
        var summary = dispatch.Summary ?? $"{dispatch.TriggerType.ToDbValue()} #{dispatch.TriggerId}";
        return $"[{age}] {projectTag}{dispatch.TargetAgent}{taskTag}: {Truncate(summary.ReplaceLineEndings(" "), 52)}";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";

    /// <summary>
    /// Reads Claude Code PID files from ~/.claude/sessions/*.json,
    /// checks which PIDs are alive, and checks out dead sessions.
    /// Only checks out sessions that have a local PID file with a dead process —
    /// never touches sessions from other machines or non-Claude agents.
    /// </summary>
    private async Task CleanupDeadSessionsAsync()
    {
        try
        {
            var sessionsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "sessions");

            if (!Directory.Exists(sessionsDir)) return;

            // Collect session IDs whose local PID is confirmed dead
            var deadSessionIds = new HashSet<string>();
            foreach (var file in Directory.GetFiles(sessionsDir, "*.json"))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("pid", out var pidProp) ||
                        !root.TryGetProperty("sessionId", out var sidProp))
                        continue;

                    var pid = pidProp.GetInt32();
                    var sessionId = sidProp.GetString();
                    if (sessionId is null) continue;

                    try
                    {
                        Process.GetProcessById(pid);
                        // PID is alive — skip
                    }
                    catch (ArgumentException)
                    {
                        // PID doesn't exist — this local session is dead
                        deadSessionIds.Add(sessionId);
                    }
                }
                catch { /* skip malformed files */ }
            }

            // Only check out sessions we can positively prove are dead locally
            foreach (var sessionId in deadSessionIds)
            {
                try
                {
                    await _client.CheckOutBySessionAsync(sessionId);
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore cleanup errors entirely */ }
    }
}

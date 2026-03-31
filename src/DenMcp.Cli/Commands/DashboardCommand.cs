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
            var dashboard = new DashboardView(client, router.Project);
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

internal sealed class DashboardView : Toplevel
{
    private readonly DenApiClient _client;
    private readonly string? _initialProject;
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
    private readonly CancellationTokenSource _cts = new();

    private List<Project> _projects = [];
    private List<TaskNode> _rootNodes = [];
    private List<Message> _messages = [];
    private List<AgentSession> _activeAgents = [];

    // Status filter: null = show all
    private string? _statusFilter;

    public DashboardView(DenApiClient client, string? initialProject)
    {
        _client = client;
        _initialProject = initialProject;

        // Project list (left sidebar)
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
            X = 0, Y = 0,
            Width = 22,
            Height = Dim.Percent(35)
        };
        _projectFrame.Add(_projectList);

        // Active agents (below projects)
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
            Width = 22,
            Height = Dim.Percent(15)
        };
        _agentFrame.Add(_agentList);

        // Task tree (main area)
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

        _taskFrame = new FrameView("Tasks")
        {
            X = 22, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(60)
        };
        _taskFrame.Add(_taskTree);

        // Message feed (bottom)
        _messageList = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false
        };

        _messageFrame = new FrameView("Messages")
        {
            X = 0, Y = Pos.Percent(60),
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };
        _messageFrame.Add(_messageList);

        // Status bar
        _statusBar = new StatusBar(new StatusItem[]
        {
            new(Key.Q | Key.CtrlMask, "~^Q~ Quit", () => Application.RequestStop()),
            new(Key.R, "~R~ Refresh", () => _ = RefreshData()),
            new(Key.S, "~S~ Status", OnChangeStatus),
            new(Key.N, "~N~ Next", OnShowNext),
            new(Key.F, "~F~ Filter", OnFilterStatus),
            new(Key.Tab, "~Tab~ Switch", CycleFocus)
        });

        Add(_projectFrame, _agentFrame, _taskFrame, _messageFrame, _statusBar);
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

                await RefreshProjectData();
            }
        }
        catch
        {
            _taskFrame.Title = "Tasks (error connecting)";
        }
    }

    private async Task RefreshProjectData()
    {
        if (_currentProject is null) return;

        try
        {
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

            // Rebuild tree preserving expansion state
            var expandedIds = CollectExpandedIds();
            _taskTree.ClearObjects();
            foreach (var node in _rootNodes)
                _taskTree.AddObject(node);

            // Re-expand previously expanded nodes
            RestoreExpansion(_rootNodes, expandedIds);

            var filterLabel = _statusFilter is not null ? $" [{_statusFilter}]" : "";
            _taskFrame.Title = $"Tasks — {_currentProject} ({CountAll(_rootNodes)}){filterLabel}";

            _messages = await _client.GetMessagesAsync(_currentProject, limit: 15);
            var msgLines = _messages.Select(m =>
            {
                var time = FormatShortTime(m.CreatedAt);
                return $"[{time}] {m.Sender}: {Truncate(m.Content.ReplaceLineEndings(" "), 50)}";
            }).ToList();
            _messageList.SetSource(msgLines);
            _messageFrame.Title = $"Messages — {_currentProject} ({_messages.Count})";

            // Clean up sessions for dead CC processes
            await CleanupDeadSessionsAsync();

            // Active agents — _global shows all agents across projects
            var isGlobal = _currentProject == "_global";
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
        catch { /* ignore refresh errors */ }
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
        if (args.Item >= 0 && args.Item < _projects.Count)
        {
            _currentProject = _projects[args.Item].Id;
            _ = RefreshProjectData();
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
        // Could be used for a preview pane in the future
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
                lines.Add($"    [{ago}] {msg.Sender}: {Truncate(msg.Content.ReplaceLineEndings(" "), 50)}");
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

    private async void OnChangeStatus()
    {
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

    private void OnFilterStatus()
    {
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

    private void CycleFocus()
    {
        if (_projectFrame.HasFocus)
            _agentFrame.SetFocus();
        else if (_agentFrame.HasFocus)
            _taskFrame.SetFocus();
        else if (_taskFrame.HasFocus)
            _messageFrame.SetFocus();
        else
            _projectFrame.SetFocus();
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

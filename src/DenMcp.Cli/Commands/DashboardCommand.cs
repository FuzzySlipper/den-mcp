using DenMcp.Core.Models;
using Terminal.Gui;
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

internal sealed class DashboardView : Toplevel
{
    private readonly DenApiClient _client;
    private readonly string? _initialProject;
    private string? _currentProject;
    private readonly FrameView _projectFrame;
    private readonly ListView _projectList;
    private readonly FrameView _taskFrame;
    private readonly ListView _taskList;
    private readonly FrameView _messageFrame;
    private readonly ListView _messageList;
    private readonly StatusBar _statusBar;
    private readonly CancellationTokenSource _cts = new();

    private List<Project> _projects = [];
    private List<TaskSummary> _tasks = [];
    private List<Message> _messages = [];

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
            Height = Dim.Percent(50)
        };
        _projectFrame.Add(_projectList);

        // Task list (main area)
        _taskList = new ListView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false
        };
        _taskList.OpenSelectedItem += OnTaskSelected;

        _taskFrame = new FrameView("Tasks")
        {
            X = 22, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(60)
        };
        _taskFrame.Add(_taskList);

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
            new(Key.Tab, "~Tab~ Switch", () => CycleFocus())
        });

        Add(_projectFrame, _taskFrame, _messageFrame, _statusBar);
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

            // Select initial or current project
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
            _tasks = await _client.ListTasksAsync(_currentProject);
            var taskLines = _tasks.Select(t =>
            {
                var icon = StatusIcon(t.Status);
                var prio = t.Priority switch { 1 => "!!", 2 => "! ", _ => "  " };
                return $"{prio}{icon} #{t.Id,-4} {Truncate(t.Title, 40)}";
            }).ToList();
            _taskList.SetSource(taskLines);
            _taskFrame.Title = $"Tasks — {_currentProject} ({_tasks.Count})";

            _messages = await _client.GetMessagesAsync(_currentProject, limit: 15);
            var msgLines = _messages.Select(m =>
            {
                var time = FormatShortTime(m.CreatedAt);
                return $"[{time}] {m.Sender}: {Truncate(m.Content.ReplaceLineEndings(" "), 50)}";
            }).ToList();
            _messageList.SetSource(msgLines);
            _messageFrame.Title = $"Messages — {_currentProject} ({_messages.Count})";
        }
        catch { /* ignore refresh errors */ }
    }

    private void OnProjectSelected(ListViewItemEventArgs args)
    {
        if (args.Item >= 0 && args.Item < _projects.Count)
        {
            _currentProject = _projects[args.Item].Id;
            _ = RefreshProjectData();
        }
    }

    private void OnTaskSelected(ListViewItemEventArgs args)
    {
        if (args.Item < 0 || args.Item >= _tasks.Count || _currentProject is null) return;
        var task = _tasks[args.Item];

        // Show task detail dialog
        var dlg = new Dialog("Task Detail", 60, 16);
        var content = $"""
            #{task.Id}: {task.Title}
            Status:   {task.Status}
            Priority: {task.Priority}
            Assigned: {task.AssignedTo ?? "(none)"}
            Deps:     {task.DependencyCount}
            Subtasks: {task.SubtaskCount}
            """;
        dlg.Add(new Label(content) { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(1) });
        var close = new Button("Close") { IsDefault = true };
        close.Clicked += () => Application.RequestStop();
        dlg.AddButton(close);
        Application.Run(dlg);
    }

    private async void OnChangeStatus()
    {
        if (_taskList.SelectedItem < 0 || _taskList.SelectedItem >= _tasks.Count || _currentProject is null) return;
        var task = _tasks[_taskList.SelectedItem];

        var statuses = new[] { "planned", "in_progress", "review", "blocked", "done", "cancelled" };
        var dlg = new Dialog("Set Status", 35, 12);
        var list = new ListView(statuses) { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(2) };
        dlg.Add(list);

        list.OpenSelectedItem += async (e) =>
        {
            var newStatus = statuses[e.Item];
            try
            {
                await _client.UpdateTaskAsync(_currentProject, task.Id, "user",
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

    private void CycleFocus()
    {
        if (_projectFrame.HasFocus)
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

    private static string StatusIcon(TaskStatus status) => status switch
    {
        TaskStatus.Planned => "[ ]",
        TaskStatus.InProgress => "[>]",
        TaskStatus.Review => "[?]",
        TaskStatus.Blocked => "[!]",
        TaskStatus.Done => "[x]",
        TaskStatus.Cancelled => "[-]",
        _ => "[ ]"
    };

    private static string FormatShortTime(DateTime dt)
    {
        var diff = DateTime.UtcNow - dt;
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h";
        return $"{(int)diff.TotalDays}d";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}

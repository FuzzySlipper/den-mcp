using DenMcp.Core.Models;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Cli.Commands;

public static class TaskCommands
{
    public static async Task<int> List(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        if (project is null) { Console.Error.WriteLine("Could not detect project. Use --project."); return 1; }

        var status = router.GetFlag("status");
        var assignedTo = router.GetFlag("assigned");
        var parentId = router.GetFlag("parent") is { } p ? int.Parse(p) : (int?)null;

        var tasks = await client.ListTasksAsync(project, status, assignedTo, parentId: parentId);
        if (tasks.Count == 0)
        {
            Console.WriteLine("No tasks found.");
            return 0;
        }

        Fmt.WriteHeader($"Tasks — {project}");
        Fmt.WriteRow(
            ("ID", 6, ConsoleColor.DarkGray),
            ("P", 3, ConsoleColor.DarkGray),
            ("STATUS", 14, ConsoleColor.DarkGray),
            ("TITLE", 50, ConsoleColor.DarkGray),
            ("ASSIGNED", 15, ConsoleColor.DarkGray));

        foreach (var t in tasks)
        {
            var idStr = t.ParentId is not null ? $"  {t.ParentId}.{t.Id}" : t.Id.ToString();
            Fmt.WriteRow(
                (idStr, 6, ConsoleColor.DarkGray),
                (t.Priority.ToString(), 3, Fmt.PriorityColor(t.Priority)),
                ($"[{Fmt.StatusIcon(t.Status)}] {t.Status.ToString().ToLowerInvariant()}", 14, Fmt.StatusColor(t.Status)),
                (t.Title, 50, ConsoleColor.White),
                (t.AssignedTo ?? "", 15, ConsoleColor.Gray));
        }

        return 0;
    }

    public static async Task<int> Get(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        var idStr = router.GetPositional(0);
        if (project is null || idStr is null || !int.TryParse(idStr, out var taskId))
        {
            Console.Error.WriteLine("Usage: den task <id> [--project <id>]");
            return 1;
        }

        var detail = await client.GetTaskAsync(project, taskId);
        var t = detail.Task;

        Fmt.WriteHeader($"Task #{t.Id}: {t.Title}");
        Console.Write("  Status:   ");
        Fmt.WriteLineColored($"[{Fmt.StatusIcon(t.Status)}] {t.Status}", Fmt.StatusColor(t.Status));
        Console.Write("  Priority: ");
        Fmt.WriteLineColored(t.Priority.ToString(), Fmt.PriorityColor(t.Priority));
        if (t.AssignedTo is not null) Console.WriteLine($"  Assigned: {t.AssignedTo}");
        if (t.Tags is { Count: > 0 }) Console.WriteLine($"  Tags:     {string.Join(", ", t.Tags)}");
        if (t.ParentId is not null) Console.WriteLine($"  Parent:   #{t.ParentId}");
        Console.WriteLine($"  Created:  {Fmt.FormatTime(t.CreatedAt)}");
        Console.WriteLine($"  Updated:  {Fmt.FormatTime(t.UpdatedAt)}");

        if (t.Description is not null)
        {
            Console.WriteLine();
            Console.WriteLine(t.Description);
        }

        if (detail.Dependencies.Count > 0)
        {
            Console.WriteLine();
            Fmt.WriteHeader("Dependencies");
            foreach (var dep in detail.Dependencies)
            {
                Console.Write($"  #{dep.TaskId} ");
                Fmt.WriteColored($"[{Fmt.StatusIcon(dep.Status)}]", Fmt.StatusColor(dep.Status));
                Console.WriteLine($" {dep.Title}");
            }
        }

        if (detail.Subtasks.Count > 0)
        {
            Console.WriteLine();
            Fmt.WriteHeader("Subtasks");
            foreach (var sub in detail.Subtasks)
            {
                Console.Write($"  #{t.Id}.{sub.Id} ");
                Fmt.WriteColored($"[{Fmt.StatusIcon(sub.Status)}]", Fmt.StatusColor(sub.Status));
                Console.WriteLine($" {sub.Title}");
            }
        }

        if (detail.RecentMessages.Count > 0)
        {
            Console.WriteLine();
            Fmt.WriteHeader("Recent Messages");
            foreach (var msg in detail.RecentMessages)
            {
                Fmt.WriteColored($"  {msg.Sender}", ConsoleColor.Cyan);
                Console.Write($" ({Fmt.FormatTime(msg.CreatedAt)}): ");
                Console.WriteLine(Fmt.Truncate(msg.Content.ReplaceLineEndings(" "), 60));
            }
        }

        return 0;
    }

    public static async Task<int> Next(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        if (project is null) { Console.Error.WriteLine("Could not detect project. Use --project."); return 1; }

        var assignedTo = router.GetFlag("agent");
        var task = await client.NextTaskAsync(project, assignedTo);

        if (task is null)
        {
            Console.WriteLine("No unblocked tasks available.");
            return 0;
        }

        Fmt.WriteHeader("Next Task");
        Console.Write($"  #{task.Id} ");
        Fmt.WriteColored($"[P{task.Priority}]", Fmt.PriorityColor(task.Priority));
        Console.WriteLine($" {task.Title}");
        if (task.Description is not null)
        {
            Console.WriteLine();
            Console.WriteLine(task.Description);
        }

        return 0;
    }

    public static async Task<int> Create(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        var title = router.GetFlag("title");
        if (project is null || title is null)
        {
            Console.Error.WriteLine("Usage: den create-task --title <title> [--project <id>] [--priority <1-5>] [--parent <id>]");
            return 1;
        }

        var priority = router.GetFlag("priority") is { } pStr ? int.Parse(pStr) : 3;
        var parentId = router.GetFlag("parent") is { } parStr ? int.Parse(parStr) : (int?)null;
        var assignedTo = router.GetFlag("assigned");
        var description = router.GetFlag("description");

        var task = await client.CreateTaskAsync(project, new ProjectTask
        {
            ProjectId = project,
            Title = title,
            Description = description,
            Priority = priority,
            AssignedTo = assignedTo,
            ParentId = parentId
        });

        Fmt.WriteColored($"Created task #{task.Id}", ConsoleColor.Green);
        Console.WriteLine($": {task.Title}");
        return 0;
    }

    public static async Task<int> SetStatus(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        var idStr = router.GetPositional(0);
        var statusStr = router.GetPositional(1);
        if (project is null || idStr is null || statusStr is null || !int.TryParse(idStr, out var taskId))
        {
            Console.Error.WriteLine("Usage: den status <task-id> <status> [--project <id>]");
            return 1;
        }

        var changes = new Dictionary<string, object?> { ["status"] = statusStr };
        var updated = await client.UpdateTaskAsync(project, taskId, "user", changes);

        Console.Write($"Task #{updated.Id} ");
        Fmt.WriteLineColored(statusStr, Fmt.StatusColor(updated.Status));
        return 0;
    }
}

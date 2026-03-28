namespace DenMcp.Cli.Commands;

public static class MessageCommands
{
    public static async Task<int> List(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        if (project is null) { Console.Error.WriteLine("Could not detect project. Use --project."); return 1; }

        var unreadFor = router.HasFlag("unread") ? "user" : null;
        var limit = router.GetFlag("limit") is { } l ? int.Parse(l) : 20;

        var messages = await client.GetMessagesAsync(project, unreadFor: unreadFor, limit: limit);
        if (messages.Count == 0)
        {
            Console.WriteLine(unreadFor is not null ? "No unread messages." : "No messages.");
            return 0;
        }

        Fmt.WriteHeader($"Messages — {project}");
        foreach (var msg in messages)
        {
            Console.Write($"  #{msg.Id} ");
            Fmt.WriteColored(msg.Sender, ConsoleColor.Cyan);
            Console.Write($" ({Fmt.FormatTime(msg.CreatedAt)})");
            if (msg.TaskId is not null)
            {
                Console.Write(" on task ");
                Fmt.WriteColored($"#{msg.TaskId}", ConsoleColor.Yellow);
            }
            if (msg.ThreadId is not null)
                Console.Write($" [reply to #{msg.ThreadId}]");
            Console.WriteLine();
            Console.Write("    ");
            Console.WriteLine(Fmt.Truncate(msg.Content.ReplaceLineEndings(" "), 70));
            Console.WriteLine();
        }

        return 0;
    }

    public static async Task<int> Send(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        var content = router.GetFlag("content");
        if (project is null || content is null)
        {
            Console.Error.WriteLine("Usage: den send --content <text> [--project <id>] [--task <id>] [--thread <id>]");
            return 1;
        }

        var sender = router.GetFlag("sender") ?? "user";
        var taskId = router.GetFlag("task") is { } t ? int.Parse(t) : (int?)null;
        var threadId = router.GetFlag("thread") is { } th ? int.Parse(th) : (int?)null;

        var msg = await client.SendMessageAsync(project, sender, content, taskId, threadId);
        Fmt.WriteColored($"Message #{msg.Id} sent", ConsoleColor.Green);
        Console.WriteLine();
        return 0;
    }
}

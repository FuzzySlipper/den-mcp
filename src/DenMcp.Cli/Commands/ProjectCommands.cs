using DenMcp.Core.Models;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Cli.Commands;

public static class ProjectCommands
{
    public static async Task<int> List(DenApiClient client)
    {
        var projects = await client.ListProjectsAsync();
        if (projects.Count == 0)
        {
            Console.WriteLine("No projects registered.");
            return 0;
        }

        Fmt.WriteHeader("Projects");
        Fmt.WriteRow(
            ("ID", 20, ConsoleColor.DarkGray),
            ("NAME", 30, ConsoleColor.DarkGray),
            ("DESCRIPTION", 40, ConsoleColor.DarkGray));

        foreach (var p in projects)
        {
            Fmt.WriteRow(
                (p.Id, 20, ConsoleColor.Cyan),
                (p.Name, 30, ConsoleColor.White),
                (p.Description ?? "", 40, ConsoleColor.Gray));
        }
        return 0;
    }

    public static async Task<int> Get(DenApiClient client, CommandRouter router)
    {
        var id = router.GetPositional(0) ?? router.Project;
        if (id is null)
        {
            Console.Error.WriteLine("Usage: den project <id>");
            return 1;
        }

        var stats = await client.GetProjectAsync(id, "user");

        Fmt.WriteHeader($"Project: {stats.Project.Name}");
        Console.WriteLine($"  ID:          {stats.Project.Id}");
        if (stats.Project.RootPath is not null)
            Console.WriteLine($"  Path:        {stats.Project.RootPath}");
        if (stats.Project.Description is not null)
            Console.WriteLine($"  Description: {stats.Project.Description}");
        Console.WriteLine();

        Console.WriteLine("  Tasks:");
        foreach (var (status, count) in stats.TaskCountsByStatus.Where(kv => kv.Value > 0))
        {
            Console.Write("    ");
            Fmt.WriteColored($"[{Fmt.StatusIcon(status)}] {status}", Fmt.StatusColor(status));
            Console.WriteLine($": {count}");
        }

        var total = stats.TaskCountsByStatus.Values.Sum();
        var done = stats.TaskCountsByStatus.GetValueOrDefault(TaskStatus.Done);
        if (total > 0)
            Console.WriteLine($"    Total: {total}  ({done * 100 / total}% done)");

        if (stats.UnreadMessageCount > 0)
        {
            Console.WriteLine();
            Fmt.WriteLineColored($"  {stats.UnreadMessageCount} unread message(s)", ConsoleColor.Yellow);
        }

        return 0;
    }
}

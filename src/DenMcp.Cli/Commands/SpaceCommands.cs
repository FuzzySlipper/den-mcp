using DenMcp.Core.Models;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Cli.Commands;

public static class SpaceCommands
{
    public static async Task<int> List(DenApiClient client, CommandRouter router)
    {
        var kind = router.GetFlag("kind");
        var includeHidden = router.HasFlag("include-hidden");
        var includeArchived = router.HasFlag("include-archived");
        var spaces = await client.ListSpacesAsync(kind: kind, includeHidden: includeHidden, includeArchived: includeArchived);

        // Default view excludes project-kind spaces to focus on non-project containers,
        // but the API returns all visible kinds unless filtered.
        var visibleSpaces = kind is null
            ? spaces.Where(s => s.Kind != "project").ToList()
            : spaces;

        if (visibleSpaces.Count == 0)
        {
            Console.WriteLine("No spaces found.");
            return 0;
        }

        Fmt.WriteHeader("Spaces");
        Fmt.WriteRow(
            ("ID", 20, ConsoleColor.DarkGray),
            ("NAME", 30, ConsoleColor.DarkGray),
            ("KIND", 14, ConsoleColor.DarkGray),
            ("VISIBILITY", 12, ConsoleColor.DarkGray));

        foreach (var s in visibleSpaces)
        {
            var kindLabel = s.Kind switch
            {
                "personal" => "personal",
                "assistant" => "assistant",
                "knowledge_base" => "knowledge-base",
                "system" => "system",
                _ => s.Kind,
            };
            var visibilityColor = s.Visibility switch
            {
                "hidden" => ConsoleColor.DarkGray,
                "archived" => ConsoleColor.DarkGray,
                _ => ConsoleColor.Green,
            };
            Fmt.WriteRow(
                (s.Id, 20, ConsoleColor.Cyan),
                (s.Name, 30, ConsoleColor.White),
                (kindLabel, 14, ConsoleColor.Yellow),
                (s.Visibility, 12, visibilityColor));
        }
        return 0;
    }

    public static async Task<int> Get(DenApiClient client, CommandRouter router)
    {
        var id = router.GetPositional(0);
        if (id is null)
        {
            Console.Error.WriteLine("Usage: den space <id>");
            return 1;
        }

        var stats = await client.GetSpaceAsync(id, "user");
        var space = stats.Project;

        Fmt.WriteHeader($"Space: {space.Name}");
        Console.WriteLine($"  ID:          {space.Id}");
        Console.WriteLine($"  Kind:        {space.Kind}");
        Console.WriteLine($"  Visibility:  {space.Visibility}");
        if (space.Owner is not null)
            Console.WriteLine($"  Owner:       {space.Owner}");
        if (space.RootPath is not null)
            Console.WriteLine($"  Path:        {space.RootPath}");
        if (space.Description is not null)
            Console.WriteLine($"  Description: {space.Description}");
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

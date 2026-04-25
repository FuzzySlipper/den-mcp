using DenMcp.Core.Models;

namespace DenMcp.Cli.Commands;

public static class AgentGuidanceCommands
{
    public static async Task<int> Run(DenApiClient client, CommandRouter router)
    {
        var action = router.GetPositional(0);
        return action switch
        {
            null or "resolve" => await Resolve(client, router),
            "list" => await List(client, router),
            "add" => await Add(client, router),
            "remove" or "delete" => await Remove(client, router),
            _ => ShowHelp()
        };
    }

    private static async Task<int> Resolve(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        if (project is null)
        {
            Console.Error.WriteLine("Usage: den guidance [resolve] [--project <id>]");
            return 1;
        }

        var guidance = await client.ResolveAgentGuidanceAsync(project);
        Console.Write(guidance.Content);
        return 0;
    }

    private static async Task<int> List(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        if (project is null)
        {
            Console.Error.WriteLine("Usage: den guidance list [--project <id>] [--include-global]");
            return 1;
        }

        var entries = await client.ListAgentGuidanceEntriesAsync(project, router.HasFlag("include-global"));
        if (entries.Count == 0)
        {
            Console.WriteLine("No agent guidance entries found.");
            return 0;
        }

        Fmt.WriteHeader("Agent guidance entries");
        Fmt.WriteRow(
            ("ID", 6, ConsoleColor.DarkGray),
            ("SCOPE", 16, ConsoleColor.DarkGray),
            ("IMPORTANCE", 12, ConsoleColor.DarkGray),
            ("ORDER", 7, ConsoleColor.DarkGray),
            ("DOCUMENT", 36, ConsoleColor.DarkGray),
            ("AUDIENCE", 24, ConsoleColor.DarkGray));

        foreach (var entry in entries)
        {
            Fmt.WriteRow(
                (entry.Id.ToString(), 6, ConsoleColor.Gray),
                (entry.ProjectId, 16, ConsoleColor.Gray),
                (entry.Importance.ToDbValue(), 12, entry.Importance == AgentGuidanceImportance.Required ? ConsoleColor.Yellow : ConsoleColor.DarkYellow),
                (entry.SortOrder.ToString(), 7, ConsoleColor.Gray),
                ($"{entry.DocumentProjectId}/{entry.DocumentSlug}", 36, ConsoleColor.Cyan),
                (entry.Audience is { Count: > 0 } ? string.Join(",", entry.Audience) : "", 24, ConsoleColor.DarkGray));
        }

        return 0;
    }

    private static async Task<int> Add(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        var slug = router.GetPositional(1);
        if (project is null || slug is null)
        {
            Console.Error.WriteLine("Usage: den guidance add <document_slug> [--project <scope>] [--doc-project <id>] [--importance required|important] [--audience a,b] [--order n] [--notes text]");
            return 1;
        }

        var importance = router.GetFlag("importance") ?? "important";
        var entry = await client.StoreAgentGuidanceEntryAsync(project, new AgentGuidanceEntry
        {
            ProjectId = project,
            DocumentProjectId = router.GetFlag("doc-project") ?? project,
            DocumentSlug = slug,
            Importance = EnumExtensions.ParseAgentGuidanceImportance(importance),
            Audience = SplitList(router.GetFlag("audience")),
            SortOrder = int.TryParse(router.GetFlag("order"), out var order) ? order : 0,
            Notes = router.GetFlag("notes")
        });

        Console.WriteLine($"Stored guidance entry #{entry.Id}: {entry.ProjectId} -> {entry.DocumentProjectId}/{entry.DocumentSlug}");
        return 0;
    }

    private static async Task<int> Remove(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        var idText = router.GetPositional(1);
        if (project is null || !int.TryParse(idText, out var id))
        {
            Console.Error.WriteLine("Usage: den guidance remove <entry_id> [--project <id>]");
            return 1;
        }

        await client.DeleteAgentGuidanceEntryAsync(project, id);
        Console.WriteLine($"Deleted guidance entry #{id}.");
        return 0;
    }

    private static List<string>? SplitList(string? value)
    {
        var items = value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items is { Length: > 0 } ? items.ToList() : null;
    }

    private static int ShowHelp()
    {
        Console.WriteLine("""
            Usage:
              den guidance [resolve] [--project <id>]
              den guidance list [--project <id>] [--include-global]
              den guidance add <document_slug> [--project <scope>] [--doc-project <id>] [--importance required|important] [--audience a,b] [--order n] [--notes text]
              den guidance remove <entry_id> [--project <id>]
            """);
        return 1;
    }
}

using DenMcp.Core.Models;

namespace DenMcp.Cli.Commands;

public static class BlackboardCommands
{
    public static async Task<int> Run(DenApiClient client, CommandRouter router)
    {
        var action = router.GetPositional(0) ?? "list";
        return action switch
        {
            "list" => await List(client, router),
            "get" => await Get(client, router),
            "set" or "save" => await Save(client, router),
            "delete" or "rm" => await Delete(client, router),
            "cleanup" => await Cleanup(client),
            _ => Usage($"Unknown blackboard action: {action}")
        };
    }

    private static async Task<int> List(DenApiClient client, CommandRouter router)
    {
        var entries = await client.ListBlackboardEntriesAsync(router.GetFlag("tags"));
        if (entries.Count == 0)
        {
            Console.WriteLine("No blackboard entries found.");
            return 0;
        }

        Fmt.WriteHeader("Shared Blackboard");
        Fmt.WriteRow(
            ("SLUG", 28, ConsoleColor.DarkGray),
            ("TITLE", 36, ConsoleColor.DarkGray),
            ("TTL", 10, ConsoleColor.DarkGray),
            ("TAGS", 24, ConsoleColor.DarkGray));

        foreach (var entry in entries)
        {
            Fmt.WriteRow(
                (entry.Slug, 28, ConsoleColor.Cyan),
                (entry.Title, 36, ConsoleColor.White),
                (entry.IdleTtlSeconds?.ToString() ?? "—", 10, ConsoleColor.DarkYellow),
                (entry.Tags is { Count: > 0 } ? string.Join(",", entry.Tags) : "—", 24, ConsoleColor.Gray));
        }

        return 0;
    }

    private static async Task<int> Get(DenApiClient client, CommandRouter router)
    {
        var slug = router.GetPositional(1);
        if (slug is null)
            return Usage("Usage: den blackboard get <slug>");

        var entry = await client.GetBlackboardEntryAsync(slug);
        if (entry is null)
        {
            Console.Error.WriteLine($"Blackboard entry '{slug}' not found.");
            return 1;
        }

        Fmt.WriteHeader(entry.Title);
        Console.WriteLine($"Slug: {entry.Slug}");
        if (entry.Tags is { Count: > 0 })
            Console.WriteLine($"Tags: {string.Join(", ", entry.Tags)}");
        if (entry.IdleTtlSeconds is not null)
            Console.WriteLine($"Idle TTL: {entry.IdleTtlSeconds}s");
        Console.WriteLine($"Updated: {Fmt.FormatTime(entry.UpdatedAt)}");
        Console.WriteLine($"Last accessed: {Fmt.FormatTime(entry.LastAccessedAt)}");
        Console.WriteLine();
        Console.WriteLine(entry.Content);
        return 0;
    }

    private static async Task<int> Save(DenApiClient client, CommandRouter router)
    {
        var slug = router.GetPositional(1);
        var title = router.GetFlag("title");
        var content = router.GetFlag("content");
        if (slug is null || title is null || content is null)
            return Usage("Usage: den blackboard set <slug> --title <title> --content <markdown> [--tags a,b] [--idle-ttl-seconds n]");

        int? idleTtlSeconds = null;
        var idleTtlText = router.GetFlag("idle-ttl-seconds");
        if (idleTtlText is not null && (!int.TryParse(idleTtlText, out var parsedTtl) || parsedTtl <= 0))
        {
            Console.Error.WriteLine("--idle-ttl-seconds must be a positive integer.");
            return 1;
        }
        if (idleTtlText is not null)
            idleTtlSeconds = int.Parse(idleTtlText);

        var tags = router.GetFlag("tags")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var entry = await client.StoreBlackboardEntryAsync(new BlackboardEntry
        {
            Slug = slug,
            Title = title,
            Content = content,
            Tags = tags,
            IdleTtlSeconds = idleTtlSeconds
        });

        Console.WriteLine($"Saved blackboard entry '{entry.Slug}'.");
        return 0;
    }

    private static async Task<int> Delete(DenApiClient client, CommandRouter router)
    {
        var slug = router.GetPositional(1);
        if (slug is null)
            return Usage("Usage: den blackboard delete <slug>");

        await client.DeleteBlackboardEntryAsync(slug);
        Console.WriteLine($"Deleted blackboard entry '{slug}'.");
        return 0;
    }

    private static async Task<int> Cleanup(DenApiClient client)
    {
        var deleted = await client.CleanupBlackboardEntriesAsync();
        Console.WriteLine($"Deleted {deleted} expired blackboard entr{(deleted == 1 ? "y" : "ies")}.");
        return 0;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Commands: den blackboard [list] [--tags a,b] | get <slug> | set <slug> --title <title> --content <markdown> [--tags a,b] [--idle-ttl-seconds n] | delete <slug> | cleanup");
        return 1;
    }
}

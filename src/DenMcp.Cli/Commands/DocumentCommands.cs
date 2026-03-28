namespace DenMcp.Cli.Commands;

public static class DocumentCommands
{
    public static async Task<int> List(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        var docType = router.GetFlag("type");

        var docs = await client.ListDocumentsAsync(project, docType);
        if (docs.Count == 0)
        {
            Console.WriteLine("No documents found.");
            return 0;
        }

        Fmt.WriteHeader("Documents");
        Fmt.WriteRow(
            ("PROJECT", 16, ConsoleColor.DarkGray),
            ("TYPE", 12, ConsoleColor.DarkGray),
            ("SLUG", 25, ConsoleColor.DarkGray),
            ("TITLE", 35, ConsoleColor.DarkGray));

        foreach (var doc in docs)
        {
            Fmt.WriteRow(
                (doc.ProjectId, 16, ConsoleColor.Gray),
                (doc.DocType.ToString().ToLowerInvariant(), 12, ConsoleColor.DarkYellow),
                (doc.Slug, 25, ConsoleColor.Cyan),
                (doc.Title, 35, ConsoleColor.White));
        }

        return 0;
    }

    public static async Task<int> Get(DenApiClient client, CommandRouter router)
    {
        var project = router.Project;
        var slug = router.GetPositional(0);
        if (project is null || slug is null)
        {
            Console.Error.WriteLine("Usage: den doc <slug> [--project <id>]");
            return 1;
        }

        var doc = await client.GetDocumentAsync(project, slug);
        if (doc is null)
        {
            Console.Error.WriteLine($"Document '{slug}' not found in project '{project}'.");
            return 1;
        }

        Fmt.WriteHeader($"{doc.Title} [{doc.DocType.ToString().ToLowerInvariant()}]");
        if (doc.Tags is { Count: > 0 })
            Console.WriteLine($"Tags: {string.Join(", ", doc.Tags)}");
        Console.WriteLine($"Updated: {Fmt.FormatTime(doc.UpdatedAt)}");
        Console.WriteLine();
        Console.WriteLine(doc.Content);

        return 0;
    }

    public static async Task<int> Search(DenApiClient client, CommandRouter router)
    {
        var query = router.GetPositional(0);
        if (query is null)
        {
            Console.Error.WriteLine("Usage: den search <query> [--project <id>]");
            return 1;
        }

        var project = router.GetFlag("project");
        var results = await client.SearchDocumentsAsync(query, project);

        if (results.Count == 0)
        {
            Console.WriteLine("No results found.");
            return 0;
        }

        Fmt.WriteHeader($"Search results for \"{query}\"");
        foreach (var r in results)
        {
            Console.Write("  ");
            Fmt.WriteColored(r.Slug, ConsoleColor.Cyan);
            Console.Write($" ({r.ProjectId}) ");
            Fmt.WriteColored($"[{r.DocType.ToString().ToLowerInvariant()}]", ConsoleColor.DarkYellow);
            Console.WriteLine();
            Console.Write("    ");
            // Strip HTML bold tags from snippet for terminal display
            var snippet = r.Snippet.Replace("<b>", "").Replace("</b>", "");
            Console.WriteLine(Fmt.Truncate(snippet.ReplaceLineEndings(" "), 70));
            Console.WriteLine();
        }

        return 0;
    }
}

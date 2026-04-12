using DenMcp.Core.Models;

namespace DenMcp.Cli.Commands;

public static class DispatchCommands
{
    public static Task<int> Run(DenApiClient client, CommandRouter router)
    {
        var positionals = router.GetPositionals("all");
        var subcommand = positionals.Count > 0 ? positionals[0] : null;

        if (subcommand is null || subcommand.Equals("list", StringComparison.OrdinalIgnoreCase))
            return List(client, router);

        if (int.TryParse(subcommand, out var directId))
            return Show(client, directId);

        return subcommand.ToLowerInvariant() switch
        {
            "show" => Show(client, router),
            "approve" => Approve(client, router),
            "reject" => Reject(client, router),
            "prompt" => Prompt(client, router),
            _ => Task.FromResult(ShowUsage())
        };
    }

    public static async Task<int> List(DenApiClient client, CommandRouter router)
    {
        var project = router.GetFlag("project");
        var targetAgent = router.GetFlag("target-agent") ?? router.GetFlag("target");
        var status = router.GetFlag("status");

        if (router.HasFlag("all") || string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            status = null;
        else if (string.IsNullOrWhiteSpace(status))
            status = "pending";

        var dispatches = await client.ListDispatchesAsync(project, targetAgent, status);
        if (dispatches.Count == 0)
        {
            Console.WriteLine("No dispatches found.");
            return 0;
        }

        var statusLabel = status ?? "all";
        Fmt.WriteHeader($"Dispatches — {statusLabel}");
        Fmt.WriteRow(
            ("ID", 6, ConsoleColor.DarkGray),
            ("PROJECT", 16, ConsoleColor.DarkGray),
            ("AGENT", 16, ConsoleColor.DarkGray),
            ("STATUS", 13, ConsoleColor.DarkGray),
            ("TASK", 8, ConsoleColor.DarkGray),
            ("AGE", 10, ConsoleColor.DarkGray),
            ("SUMMARY", 52, ConsoleColor.DarkGray));

        foreach (var dispatch in dispatches)
        {
            Fmt.WriteRow(
                (dispatch.Id.ToString(), 6, ConsoleColor.DarkGray),
                (dispatch.ProjectId, 16, ConsoleColor.Cyan),
                (dispatch.TargetAgent, 16, ConsoleColor.White),
                ($"[{Fmt.DispatchStatusIcon(dispatch.Status)}] {dispatch.Status.ToDbValue()}", 13, Fmt.DispatchStatusColor(dispatch.Status)),
                (dispatch.TaskId is not null ? $"#{dispatch.TaskId}" : "", 8, ConsoleColor.Yellow),
                (Fmt.FormatTime(dispatch.CreatedAt), 10, ConsoleColor.Gray),
                (dispatch.Summary ?? $"{dispatch.TriggerType.ToDbValue()} #{dispatch.TriggerId}", 52, ConsoleColor.Gray));
        }

        return 0;
    }

    public static Task<int> Show(DenApiClient client, CommandRouter router) =>
        Show(client, GetDispatchId(router, "show"));

    public static async Task<int> Show(DenApiClient client, int dispatchId)
    {
        if (dispatchId <= 0)
            return ShowUsage("Usage: den dispatch show <id>");

        var dispatch = await client.GetDispatchAsync(dispatchId);
        PrintDispatchDetail(dispatch, includePrompt: true);
        return 0;
    }

    public static async Task<int> Approve(DenApiClient client, CommandRouter router)
    {
        var dispatchId = GetDispatchId(router, "approve");
        if (dispatchId <= 0)
            return ShowUsage("Usage: den dispatch approve <id> [--by <identity>]");

        var decidedBy = router.GetFlag("by") ?? router.GetFlag("decided-by") ?? "user";
        var dispatch = await client.ApproveDispatchAsync(dispatchId, decidedBy);

        Console.Write($"Approved dispatch #{dispatch.Id} ");
        Fmt.WriteColored($"for {dispatch.TargetAgent}", ConsoleColor.Cyan);
        Console.WriteLine($" on {dispatch.ProjectId}");
        return 0;
    }

    public static async Task<int> Reject(DenApiClient client, CommandRouter router)
    {
        var dispatchId = GetDispatchId(router, "reject");
        if (dispatchId <= 0)
            return ShowUsage("Usage: den dispatch reject <id> [--by <identity>]");

        var decidedBy = router.GetFlag("by") ?? router.GetFlag("decided-by") ?? "user";
        var dispatch = await client.RejectDispatchAsync(dispatchId, decidedBy);

        Console.Write($"Rejected dispatch #{dispatch.Id} ");
        Fmt.WriteColored($"for {dispatch.TargetAgent}", ConsoleColor.Cyan);
        Console.WriteLine($" on {dispatch.ProjectId}");
        return 0;
    }

    public static async Task<int> Prompt(DenApiClient client, CommandRouter router)
    {
        var dispatchId = GetDispatchId(router, "prompt");
        if (dispatchId <= 0)
            return ShowUsage("Usage: den dispatch prompt <id>");

        var dispatch = await client.GetDispatchAsync(dispatchId);
        if (string.IsNullOrWhiteSpace(dispatch.ContextPrompt))
        {
            Console.Error.WriteLine($"Dispatch #{dispatch.Id} does not have a generated prompt.");
            return 1;
        }

        Console.WriteLine(dispatch.ContextPrompt);
        return 0;
    }

    private static void PrintDispatchDetail(DispatchEntry dispatch, bool includePrompt)
    {
        Fmt.WriteHeader($"Dispatch #{dispatch.Id}");
        Console.Write("  Status:   ");
        Fmt.WriteLineColored(dispatch.Status.ToDbValue(), Fmt.DispatchStatusColor(dispatch.Status));
        Console.WriteLine($"  Project:  {dispatch.ProjectId}");
        Console.WriteLine($"  Agent:    {dispatch.TargetAgent}");
        Console.WriteLine($"  Trigger:  {dispatch.TriggerType.ToDbValue()} #{dispatch.TriggerId}");
        Console.WriteLine($"  Task:     {(dispatch.TaskId is not null ? $"#{dispatch.TaskId}" : "(none)")}");
        Console.WriteLine($"  Created:  {Fmt.FormatTime(dispatch.CreatedAt)}");
        if (dispatch.ExpiresAt != default)
            Console.WriteLine($"  Expires:  {dispatch.ExpiresAt:yyyy-MM-dd HH:mm} UTC");
        if (dispatch.DecidedAt is not null)
            Console.WriteLine($"  Decided:  {dispatch.DecidedAt:yyyy-MM-dd HH:mm} UTC by {dispatch.DecidedBy ?? "unknown"}");
        if (dispatch.CompletedAt is not null)
            Console.WriteLine($"  Completed:{dispatch.CompletedAt:yyyy-MM-dd HH:mm} UTC by {dispatch.CompletedBy ?? "unknown"}");

        if (!string.IsNullOrWhiteSpace(dispatch.Summary))
        {
            Console.WriteLine();
            Console.WriteLine(dispatch.Summary);
        }

        if (includePrompt)
        {
            Console.WriteLine();
            Fmt.WriteHeader("Prompt");
            Console.WriteLine(dispatch.ContextPrompt ?? "(none stored)");
        }
    }

    private static int GetDispatchId(CommandRouter router, string subcommand)
    {
        var positionals = router.GetPositionals("all");
        var idText = positionals.Count > 1 ? positionals[1] : null;
        return int.TryParse(idText, out var dispatchId) ? dispatchId : 0;
    }

    private static int ShowUsage(string? usage = null)
    {
        if (usage is not null)
            Console.Error.WriteLine(usage);
        else
            Console.Error.WriteLine("""
                Usage:
                  den dispatch [--project <id>] [--status <status>|--all] [--target-agent <identity>]
                  den dispatch show <id>
                  den dispatch approve <id> [--by <identity>]
                  den dispatch reject <id> [--by <identity>]
                  den dispatch prompt <id>
                """);
        return 1;
    }
}

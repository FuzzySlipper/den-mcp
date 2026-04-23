using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class AgentStreamRoutes
{
    public static void MapAgentStreamRoutes(this WebApplication app)
    {
        app.MapGet("/api/agent-stream", async (
            IAgentStreamRepository repo,
            string? projectId,
            int? taskId,
            int? dispatchId,
            string? streamKind,
            string? eventType,
            string? sender,
            string? senderInstanceId,
            string? recipientAgent,
            string? recipientRole,
            string? recipientInstanceId,
            int? limit) =>
        {
            var parsedKind = TryParseKind(streamKind, out var kind, out var error);
            if (!parsedKind)
                return Results.BadRequest(new { error });

            var entries = await repo.ListAsync(new AgentStreamListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                DispatchId = dispatchId,
                StreamKind = kind,
                EventType = eventType,
                Sender = sender,
                SenderInstanceId = senderInstanceId,
                RecipientAgent = recipientAgent,
                RecipientRole = recipientRole,
                RecipientInstanceId = recipientInstanceId,
                Limit = limit ?? 50
            });

            return Results.Ok(entries);
        });

        app.MapGet("/api/agent-stream/{entryId:int}", async (IAgentStreamRepository repo, int entryId) =>
        {
            var entry = await repo.GetByIdAsync(entryId);
            return entry is not null
                ? Results.Ok(entry)
                : Results.NotFound(new { error = $"Agent stream entry {entryId} not found" });
        });

        var group = app.MapGroup("/api/projects/{projectId}/agent-stream");

        group.MapGet("/", async (
            IAgentStreamRepository repo,
            string projectId,
            int? taskId,
            int? dispatchId,
            string? streamKind,
            string? eventType,
            string? sender,
            string? senderInstanceId,
            string? recipientAgent,
            string? recipientRole,
            string? recipientInstanceId,
            int? limit) =>
        {
            var parsedKind = TryParseKind(streamKind, out var kind, out var error);
            if (!parsedKind)
                return Results.BadRequest(new { error });

            var entries = await repo.ListAsync(new AgentStreamListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                DispatchId = dispatchId,
                StreamKind = kind,
                EventType = eventType,
                Sender = sender,
                SenderInstanceId = senderInstanceId,
                RecipientAgent = recipientAgent,
                RecipientRole = recipientRole,
                RecipientInstanceId = recipientInstanceId,
                Limit = limit ?? 50
            });

            return Results.Ok(entries);
        });

        group.MapGet("/{entryId:int}", async (IAgentStreamRepository repo, string projectId, int entryId) =>
        {
            var entry = await repo.GetByIdAsync(entryId);
            if (entry is null || !string.Equals(entry.ProjectId, projectId, StringComparison.Ordinal))
                return Results.NotFound(new { error = $"Agent stream entry {entryId} not found" });

            return Results.Ok(entry);
        });
    }

    private static bool TryParseKind(string? streamKind, out AgentStreamKind? parsedKind, out string? error)
    {
        parsedKind = null;
        error = null;

        if (string.IsNullOrWhiteSpace(streamKind))
            return true;

        try
        {
            parsedKind = EnumExtensions.ParseAgentStreamKind(streamKind);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

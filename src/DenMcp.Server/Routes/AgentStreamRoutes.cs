using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

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

        app.MapPost("/api/agent-stream/messages", async (IAgentStreamMessageService service, SendAgentStreamMessageRequest req) =>
        {
            try
            {
                var result = await service.CreateAsync(ToCreateRequest(req, req.ProjectId));
                return Results.Created($"/api/agent-stream/{result.Entry.Id}", result);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid metadata JSON: {ex.Message}" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/agent-stream/ops", async (IAgentStreamRepository repo, SendAgentStreamOpsRequest req) =>
        {
            try
            {
                var result = await repo.AppendAsync(ToOpsEntry(req, req.ProjectId));
                return Results.Created($"/api/agent-stream/{result.Id}", result);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid metadata JSON: {ex.Message}" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
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

        group.MapPost("/messages", async (IAgentStreamMessageService service, string projectId, SendAgentStreamMessageRequest req) =>
        {
            if (!string.IsNullOrWhiteSpace(req.ProjectId) &&
                !string.Equals(req.ProjectId, projectId, StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    error = $"project_id '{req.ProjectId}' does not match route project '{projectId}'."
                });
            }

            try
            {
                var result = await service.CreateAsync(ToCreateRequest(req, projectId));
                return Results.Created($"/api/projects/{projectId}/agent-stream/{result.Entry.Id}", result);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid metadata JSON: {ex.Message}" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/ops", async (IAgentStreamRepository repo, string projectId, SendAgentStreamOpsRequest req) =>
        {
            if (!string.IsNullOrWhiteSpace(req.ProjectId) &&
                !string.Equals(req.ProjectId, projectId, StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    error = $"project_id '{req.ProjectId}' does not match route project '{projectId}'."
                });
            }

            try
            {
                var result = await repo.AppendAsync(ToOpsEntry(req, projectId));
                return Results.Created($"/api/projects/{projectId}/agent-stream/{result.Id}", result);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid metadata JSON: {ex.Message}" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
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

    private static AgentStreamMessageCreateRequest ToCreateRequest(SendAgentStreamMessageRequest req, string? projectId)
    {
        JsonElement? metadata = null;
        if (!string.IsNullOrWhiteSpace(req.Metadata))
            metadata = JsonSerializer.Deserialize<JsonElement>(req.Metadata);

        return new AgentStreamMessageCreateRequest
        {
            ProjectId = projectId,
            TaskId = req.TaskId,
            ThreadId = req.ThreadId,
            DispatchId = req.DispatchId,
            Sender = req.Sender,
            SenderInstanceId = req.SenderInstanceId,
            EventType = req.EventType,
            RecipientAgent = req.RecipientAgent,
            RecipientRole = req.RecipientRole,
            RecipientInstanceId = req.RecipientInstanceId,
            DeliveryMode = req.DeliveryMode,
            Body = req.Body,
            Metadata = metadata,
            DedupKey = req.DedupKey
        };
    }

    private static AgentStreamEntry ToOpsEntry(SendAgentStreamOpsRequest req, string? projectId)
    {
        var eventType = NormalizeRequired(req.EventType, nameof(req.EventType));
        var sender = NormalizeRequired(req.Sender, nameof(req.Sender));

        JsonElement? metadata = null;
        if (!string.IsNullOrWhiteSpace(req.Metadata))
            metadata = JsonSerializer.Deserialize<JsonElement>(req.Metadata);

        return new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = eventType,
            ProjectId = NormalizeOptional(projectId),
            TaskId = req.TaskId,
            ThreadId = req.ThreadId,
            DispatchId = req.DispatchId,
            Sender = sender,
            SenderInstanceId = NormalizeOptional(req.SenderInstanceId),
            RecipientAgent = NormalizeOptional(req.RecipientAgent),
            RecipientRole = NormalizeOptional(req.RecipientRole),
            RecipientInstanceId = NormalizeOptional(req.RecipientInstanceId),
            DeliveryMode = req.DeliveryMode ?? AgentStreamDeliveryMode.RecordOnly,
            Body = NormalizeOptional(req.Body),
            Metadata = metadata,
            DedupKey = NormalizeOptional(req.DedupKey)
        };
    }

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = NormalizeOptional(value);
        return normalized ?? throw new InvalidOperationException($"{paramName} is required.");
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record SendAgentStreamMessageRequest(
    string Sender,
    string EventType,
    string Body,
    string? ProjectId = null,
    int? TaskId = null,
    int? ThreadId = null,
    int? DispatchId = null,
    string? SenderInstanceId = null,
    string? RecipientAgent = null,
    string? RecipientRole = null,
    string? RecipientInstanceId = null,
    AgentStreamDeliveryMode? DeliveryMode = null,
    string? Metadata = null,
    string? DedupKey = null);

public sealed record SendAgentStreamOpsRequest(
    string Sender,
    string EventType,
    string? ProjectId = null,
    int? TaskId = null,
    int? ThreadId = null,
    int? DispatchId = null,
    string? SenderInstanceId = null,
    string? RecipientAgent = null,
    string? RecipientRole = null,
    string? RecipientInstanceId = null,
    AgentStreamDeliveryMode? DeliveryMode = null,
    string? Body = null,
    string? Metadata = null,
    string? DedupKey = null);

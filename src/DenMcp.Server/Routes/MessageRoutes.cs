using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging;

namespace DenMcp.Server.Routes;

public static class MessageRoutes
{
    public static void MapMessageRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/messages");

        group.MapPost("/", async (IMessageRepository repo, IDispatchDetectionService detection,
            ILoggerFactory loggers, string projectId, SendMessageRequest req) =>
        {
            var msg = new Message
            {
                ProjectId = projectId,
                Sender = req.Sender,
                Content = req.Content,
                TaskId = req.TaskId,
                ThreadId = req.ThreadId,
                Metadata = req.Metadata is not null ? JsonSerializer.Deserialize<JsonElement>(req.Metadata) : null
            };
            var created = await repo.CreateAsync(msg);
            try
            {
                await detection.OnMessageCreatedAsync(created);
            }
            catch (Exception ex)
            {
                loggers.CreateLogger("DispatchDetection")
                    .LogError(ex, "Dispatch detection failed for message {MessageId}", created.Id);
            }
            return Results.Created($"/api/projects/{projectId}/messages/{created.Id}", created);
        });

        group.MapGet("/", async (IMessageRepository repo, string projectId,
            int? taskId, string? since, string? unreadFor, int? limit) =>
        {
            DateTime? sinceDate = since is not null ? DateTime.Parse(since) : null;
            var messages = await repo.GetMessagesAsync(projectId, taskId, sinceDate, unreadFor, limit ?? 20);
            return Results.Ok(messages);
        });

        group.MapGet("/thread/{threadId:int}", async (IMessageRepository repo, string projectId, int threadId) =>
        {
            try
            {
                var thread = await repo.GetThreadAsync(threadId);
                if (thread.Root.ProjectId != projectId)
                    return Results.NotFound(new { error = $"Message {threadId} not found" });
                return Results.Ok(thread);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Message {threadId} not found" });
            }
        });

        app.MapPost("/api/messages/mark-read", async (IMessageRepository repo, MarkReadRequest req) =>
        {
            var count = await repo.MarkReadAsync(req.Agent, req.MessageIds);
            return Results.Ok(new { marked = count });
        });
    }
}

public record SendMessageRequest(
    string Sender,
    string Content,
    int? TaskId = null,
    int? ThreadId = null,
    string? Metadata = null);

public record MarkReadRequest(string Agent, int[] MessageIds);

using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server.Realtime;

namespace DenMcp.Server.Routes;

public static class SubagentRunRoutes
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static void MapSubagentRunRoutes(this WebApplication app)
    {
        app.MapGet("/api/subagent-runs", async (
            ISubagentRunService service,
            string? projectId,
            int? taskId,
            string? state,
            int? limit) =>
        {
            var runs = await service.ListAsync(new SubagentRunListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                State = state,
                Limit = limit ?? 8
            });

            return Results.Ok(runs);
        });

        app.MapGet("/api/subagent-runs/events", StreamSubagentRunEvents);

        app.MapGet("/api/subagent-runs/{runId}", async (
            ISubagentRunService service,
            string runId,
            string? projectId,
            int? taskId) =>
        {
            var detail = await service.GetAsync(runId, new SubagentRunListOptions
            {
                ProjectId = projectId,
                TaskId = taskId
            });

            return detail is not null
                ? Results.Ok(detail)
                : Results.NotFound(new { error = $"Sub-agent run {runId} not found" });
        });

        app.MapGet("/api/projects/{projectId}/subagent-runs", async (
            ISubagentRunService service,
            string projectId,
            int? taskId,
            string? state,
            int? limit) =>
        {
            var runs = await service.ListAsync(new SubagentRunListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                State = state,
                Limit = limit ?? 8
            });

            return Results.Ok(runs);
        });

        app.MapGet("/api/projects/{projectId}/subagent-runs/events", async (
            HttpContext context,
            AgentStreamRealtimeHub realtime,
            string projectId,
            int? taskId) =>
        {
            await StreamSubagentRunEvents(context, realtime, projectId, taskId);
        });

        app.MapGet("/api/projects/{projectId}/subagent-runs/{runId}", async (
            ISubagentRunService service,
            string projectId,
            string runId,
            int? taskId) =>
        {
            var detail = await service.GetAsync(runId, new SubagentRunListOptions
            {
                ProjectId = projectId,
                TaskId = taskId
            });

            return detail is not null
                ? Results.Ok(detail)
                : Results.NotFound(new { error = $"Sub-agent run {runId} not found" });
        });
    }

    private static async Task StreamSubagentRunEvents(
        HttpContext context,
        AgentStreamRealtimeHub realtime,
        string? projectId,
        int? taskId)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        await context.Response.StartAsync(context.RequestAborted);
        await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);

        try
        {
            await foreach (var entry in realtime.SubscribeAsync(
                new AgentStreamRealtimeFilter(projectId, taskId, EventTypePrefix: "subagent_"),
                context.RequestAborted))
            {
                await WriteSseEventAsync(context.Response, "subagent_run_updated", entry, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected.
        }
    }

    private static async Task WriteSseEventAsync(
        HttpResponse response,
        string eventName,
        AgentStreamEntry entry,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(entry, SseJsonOptions);
        await response.WriteAsync($"id: {entry.Id}\n", cancellationToken);
        await response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await response.WriteAsync($"data: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

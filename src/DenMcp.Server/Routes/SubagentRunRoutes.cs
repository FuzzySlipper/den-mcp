using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Server.Routes;

public static class SubagentRunRoutes
{
    public static void MapSubagentRunRoutes(this WebApplication app)
    {
        app.MapGet("/api/subagent-runs", async (
            ISubagentRunService service,
            string? projectId,
            int? taskId,
            int? limit) =>
        {
            var runs = await service.ListAsync(new SubagentRunListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                Limit = limit ?? 8
            });

            return Results.Ok(runs);
        });

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
            int? limit) =>
        {
            var runs = await service.ListAsync(new SubagentRunListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                Limit = limit ?? 8
            });

            return Results.Ok(runs);
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
}

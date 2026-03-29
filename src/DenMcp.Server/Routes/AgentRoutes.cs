using DenMcp.Core.Data;

namespace DenMcp.Server.Routes;

public static class AgentRoutes
{
    public static void MapAgentRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/agents");

        group.MapPost("/checkin", async (IAgentSessionRepository repo, CheckInRequest req) =>
        {
            var session = await repo.CheckInAsync(req.Agent, req.ProjectId, req.Metadata);
            return Results.Ok(session);
        });

        group.MapPost("/heartbeat", async (IAgentSessionRepository repo, HeartbeatRequest req) =>
        {
            var ok = await repo.HeartbeatAsync(req.Agent, req.ProjectId);
            return ok
                ? Results.Ok(new { status = "ok" })
                : Results.NotFound(new { error = "No active session found. Call checkin first." });
        });

        group.MapPost("/checkout", async (IAgentSessionRepository repo, CheckOutRequest req) =>
        {
            var ok = await repo.CheckOutAsync(req.Agent, req.ProjectId);
            return ok
                ? Results.Ok(new { status = "checked_out" })
                : Results.NotFound(new { error = "No active session found." });
        });

        group.MapGet("/active", async (IAgentSessionRepository repo, string? projectId) =>
        {
            var sessions = await repo.ListActiveAsync(projectId);
            return Results.Ok(sessions);
        });
    }
}

public record CheckInRequest(string Agent, string ProjectId, string? Metadata = null);
public record HeartbeatRequest(string Agent, string ProjectId);
public record CheckOutRequest(string Agent, string ProjectId);

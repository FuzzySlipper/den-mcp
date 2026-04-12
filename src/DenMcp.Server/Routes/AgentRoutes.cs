using DenMcp.Core.Data;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging;

namespace DenMcp.Server.Routes;

public static class AgentRoutes
{
    public static void MapAgentRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/agents");

        group.MapPost("/checkin", async (IAgentSessionRepository repo, INotificationChannel notifications,
            ILoggerFactory loggers, CheckInRequest req) =>
        {
            var session = await repo.CheckInAsync(req.Agent, req.ProjectId, req.SessionId, req.Metadata);
            try
            {
                await notifications.SendAgentStatusAsync(req.ProjectId, req.Agent, "checked_in");
            }
            catch (Exception ex)
            {
                loggers.CreateLogger("Notifications")
                    .LogError(ex, "Agent check-in notification failed for {Agent} on {ProjectId}", req.Agent, req.ProjectId);
            }
            return Results.Ok(session);
        });

        group.MapPost("/heartbeat", async (IAgentSessionRepository repo, HeartbeatRequest req) =>
        {
            var ok = await repo.HeartbeatAsync(req.Agent, req.ProjectId);
            return ok
                ? Results.Ok(new { status = "ok" })
                : Results.NotFound(new { error = "No active session found. Call checkin first." });
        });

        group.MapPost("/checkout", async (IAgentSessionRepository repo, INotificationChannel notifications,
            ILoggerFactory loggers, CheckOutRequest req) =>
        {
            bool ok;
            if (req.SessionId is not null)
                ok = await repo.CheckOutBySessionAsync(req.SessionId);
            else
                ok = await repo.CheckOutAsync(req.Agent, req.ProjectId);

            if (ok)
            {
                try
                {
                    await notifications.SendAgentStatusAsync(req.ProjectId, req.Agent, "checked_out");
                }
                catch (Exception ex)
                {
                    loggers.CreateLogger("Notifications")
                        .LogError(ex, "Agent checkout notification failed for {Agent} on {ProjectId}", req.Agent, req.ProjectId);
                }
            }

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

public record CheckInRequest(string Agent, string ProjectId, string? SessionId = null, string? Metadata = null);
public record HeartbeatRequest(string Agent, string ProjectId);
public record CheckOutRequest(string Agent, string ProjectId, string? SessionId = null);

using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class AgentWorkspaceRoutes
{
    public static void MapAgentWorkspaceRoutes(this WebApplication app)
    {
        app.MapGet("/api/agent-workspaces", async (
            IAgentWorkspaceRepository repo,
            string? projectId,
            int? taskId,
            string? state,
            int? limit) =>
        {
            var parsedState = ParseState(state);
            if (parsedState.Invalid)
                return Results.BadRequest(new { error = $"Unknown agent workspace state: {state}" });

            var workspaces = await repo.ListAsync(new AgentWorkspaceListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                State = parsedState.State,
                Limit = limit ?? 50
            });
            return Results.Ok(workspaces);
        });

        app.MapGet("/api/agent-workspaces/{workspaceId}", async (
            IAgentWorkspaceRepository repo,
            string workspaceId,
            string? projectId) =>
        {
            var workspace = await repo.GetAsync(workspaceId, projectId);
            return workspace is null
                ? Results.NotFound(new { error = $"Agent workspace {workspaceId} not found" })
                : Results.Ok(workspace);
        });

        app.MapGet("/api/projects/{projectId}/agent-workspaces", async (
            IAgentWorkspaceRepository repo,
            string projectId,
            int? taskId,
            string? state,
            int? limit) =>
        {
            var parsedState = ParseState(state);
            if (parsedState.Invalid)
                return Results.BadRequest(new { error = $"Unknown agent workspace state: {state}" });

            var workspaces = await repo.ListAsync(new AgentWorkspaceListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                State = parsedState.State,
                Limit = limit ?? 50
            });
            return Results.Ok(workspaces);
        });

        app.MapGet("/api/projects/{projectId}/agent-workspaces/{workspaceId}", async (
            IAgentWorkspaceRepository repo,
            string projectId,
            string workspaceId) =>
        {
            var workspace = await repo.GetAsync(workspaceId, projectId);
            return workspace is null
                ? Results.NotFound(new { error = $"Agent workspace {workspaceId} not found" })
                : Results.Ok(workspace);
        });

        app.MapPost("/api/projects/{projectId}/agent-workspaces", async (
            IAgentWorkspaceRepository repo,
            string projectId,
            UpsertAgentWorkspaceRequest req) =>
        {
            var workspace = BuildWorkspace(projectId, req);
            var validationError = Validate(workspace);
            if (validationError is not null)
                return Results.BadRequest(new { error = validationError });

            return await SaveWorkspaceAsync(repo, workspace);
        });

        app.MapPut("/api/projects/{projectId}/agent-workspaces/{workspaceId}", async (
            IAgentWorkspaceRepository repo,
            string projectId,
            string workspaceId,
            UpsertAgentWorkspaceRequest req) =>
        {
            var workspace = BuildWorkspace(projectId, req with { Id = workspaceId });
            var validationError = Validate(workspace);
            if (validationError is not null)
                return Results.BadRequest(new { error = validationError });

            return await SaveWorkspaceAsync(repo, workspace);
        });
    }

    private static async Task<IResult> SaveWorkspaceAsync(IAgentWorkspaceRepository repo, AgentWorkspace workspace)
    {
        try
        {
            var saved = await repo.UpsertAsync(workspace);
            return Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Results.BadRequest(new { error = "Agent workspace references an unknown project, task, or run." });
        }
    }

    private static AgentWorkspace BuildWorkspace(string projectId, UpsertAgentWorkspaceRequest req) => new()
    {
        Id = string.IsNullOrWhiteSpace(req.Id) ? NewWorkspaceId() : req.Id.Trim(),
        ProjectId = projectId.Trim(),
        TaskId = req.TaskId,
        Branch = req.Branch?.Trim() ?? string.Empty,
        WorktreePath = req.WorktreePath?.Trim() ?? string.Empty,
        BaseBranch = req.BaseBranch?.Trim() ?? string.Empty,
        BaseCommit = TrimToNull(req.BaseCommit),
        HeadCommit = TrimToNull(req.HeadCommit),
        State = req.State ?? AgentWorkspaceState.Active,
        CreatedByRunId = TrimToNull(req.CreatedByRunId),
        DevServerUrl = TrimToNull(req.DevServerUrl),
        PreviewUrl = TrimToNull(req.PreviewUrl),
        CleanupPolicy = req.CleanupPolicy ?? AgentWorkspaceCleanupPolicy.Keep,
        ChangedFileSummary = req.ChangedFileSummary is null ? null : req.ChangedFileSummary.Value.Clone()
    };

    private static string NewWorkspaceId() => $"ws_{Guid.NewGuid():N}";

    private static (AgentWorkspaceState? State, bool Invalid) ParseState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return (null, false);

        try
        {
            return (EnumExtensions.ParseAgentWorkspaceState(state.Trim()), false);
        }
        catch (ArgumentException)
        {
            return (null, true);
        }
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Validate(AgentWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.ProjectId))
            return "Project id is required.";
        if (workspace.TaskId <= 0)
            return "Task id must be positive.";
        if (string.IsNullOrWhiteSpace(workspace.Branch))
            return "Branch is required.";
        if (string.IsNullOrWhiteSpace(workspace.WorktreePath))
            return "Worktree path is required.";
        if (string.IsNullOrWhiteSpace(workspace.BaseBranch))
            return "Base branch is required.";
        if (!IsCompactChangedFileSummary(workspace.ChangedFileSummary))
            return "Changed file summary must be compact JSON at or below 12000 characters.";
        return null;
    }

    private static bool IsCompactChangedFileSummary(JsonElement? summary)
    {
        if (summary is null)
            return true;
        return JsonSerializer.Serialize(summary.Value).Length <= 12_000;
    }
}

public sealed record UpsertAgentWorkspaceRequest
{
    public string? Id { get; init; }
    public int TaskId { get; init; }
    public string? Branch { get; init; }
    public string? WorktreePath { get; init; }
    public string? BaseBranch { get; init; }
    public string? BaseCommit { get; init; }
    public string? HeadCommit { get; init; }
    public AgentWorkspaceState? State { get; init; }
    public string? CreatedByRunId { get; init; }
    public string? DevServerUrl { get; init; }
    public string? PreviewUrl { get; init; }
    public AgentWorkspaceCleanupPolicy? CleanupPolicy { get; init; }
    public JsonElement? ChangedFileSummary { get; init; }
}

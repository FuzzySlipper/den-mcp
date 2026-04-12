using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Server.Routes;

public static class TaskRoutes
{
    public static void MapTaskRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/tasks");

        group.MapPost("/", async (ITaskRepository repo, string projectId, CreateTaskRequest req) =>
        {
            var task = await repo.CreateAsync(new ProjectTask
            {
                ProjectId = projectId,
                Title = req.Title,
                Description = req.Description,
                Priority = req.Priority ?? 3,
                Tags = req.Tags,
                AssignedTo = req.AssignedTo,
                ParentId = req.ParentId
            }, req.DependsOn);
            return Results.Created($"/api/projects/{projectId}/tasks/{task.Id}", task);
        });

        group.MapGet("/", async (ITaskRepository repo, string projectId,
            string? status, string? assignedTo, string? tags, int? priority, int? parentId, bool? tree) =>
        {
            var statuses = status?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(EnumExtensions.ParseTaskStatus).ToArray();
            var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tasks = await repo.ListAsync(projectId, statuses, assignedTo, tagList, priority, parentId,
                includeAll: tree == true && parentId is null);
            return Results.Ok(tasks);
        });

        group.MapGet("/{taskId:int}", async (ITaskRepository repo, string projectId, int taskId) =>
        {
            try
            {
                var detail = await repo.GetDetailAsync(taskId);
                if (detail.Task.ProjectId != projectId)
                    return Results.NotFound(new { error = $"Task {taskId} not found" });
                return Results.Ok(detail);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Task {taskId} not found" });
            }
        });

        group.MapPut("/{taskId:int}", async (ITaskRepository repo, IDispatchDetectionService detection,
            ILoggerFactory loggers, string projectId, int taskId, UpdateTaskRequest req) =>
        {
            var task = await repo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            var oldStatus = task.Status.ToDbValue();

            var changes = new Dictionary<string, object?>();
            if (req.Title is not null) changes["title"] = req.Title;
            if (req.Description is not null) changes["description"] = req.Description;
            if (req.Status is not null) changes["status"] = EnumExtensions.ParseTaskStatus(req.Status);
            if (req.Priority is not null) changes["priority"] = req.Priority.Value;
            if (req.AssignedTo is not null) changes["assigned_to"] = req.AssignedTo;
            if (req.Tags is not null) changes["tags"] = req.Tags;
            if (req.ParentId is not null) changes["parent_id"] = req.ParentId.Value;

            try
            {
                var updated = await repo.UpdateAsync(taskId, changes, req.Agent);

                if (req.Status is not null && req.Status != oldStatus)
                {
                    try
                    {
                        await detection.OnTaskStatusChangedAsync(updated, oldStatus, req.Status, req.Agent);
                    }
                    catch (Exception ex)
                    {
                        loggers.CreateLogger("DispatchDetection")
                            .LogError(ex, "Dispatch detection failed for task {TaskId}", taskId);
                    }
                }

                return Results.Ok(updated);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Task {taskId} not found" });
            }
        });

        group.MapGet("/next", async (ITaskRepository repo, string projectId, string? assignedTo) =>
        {
            var next = await repo.GetNextTaskAsync(projectId, assignedTo);
            return next is not null
                ? Results.Ok(next)
                : Results.Ok(new { message = "No unblocked tasks available." });
        });

        group.MapPost("/{taskId:int}/dependencies", async (ITaskRepository repo, string projectId, int taskId, AddDependencyRequest req) =>
        {
            var task = await repo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });
            var dep = await repo.GetByIdAsync(req.DependsOn);
            if (dep is null || dep.ProjectId != projectId)
                return Results.BadRequest(new { error = $"Dependency target task {req.DependsOn} not found in project {projectId}." });

            try
            {
                await repo.AddDependencyAsync(taskId, req.DependsOn);
                return Results.Ok(new { message = $"Task {taskId} now depends on task {req.DependsOn}." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{taskId:int}/dependencies/{dependsOn:int}", async (ITaskRepository repo, string projectId, int taskId, int dependsOn) =>
        {
            var task = await repo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            await repo.RemoveDependencyAsync(taskId, dependsOn);
            return Results.Ok(new { message = $"Removed dependency." });
        });
    }
}

public record CreateTaskRequest(
    string Title,
    string? Description = null,
    int? Priority = null,
    List<string>? Tags = null,
    string? AssignedTo = null,
    int[]? DependsOn = null,
    int? ParentId = null);

public record UpdateTaskRequest(
    string Agent,
    string? Title = null,
    string? Description = null,
    string? Status = null,
    int? Priority = null,
    string? AssignedTo = null,
    List<string>? Tags = null,
    int? ParentId = null);

public record AddDependencyRequest(int DependsOn);

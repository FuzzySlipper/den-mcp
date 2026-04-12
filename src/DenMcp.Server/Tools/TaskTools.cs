using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using ModelContextProtocol.Server;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class TaskTools
{
    [McpServerTool(Name = "create_task"), Description("Create a new task or subtask in a project.")]
    public static async Task<string> CreateTask(
        ITaskRepository repo,
        [Description("Project ID.")] string project_id,
        [Description("Task title.")] string title,
        [Description("Detailed description / acceptance criteria (markdown).")] string? description = null,
        [Description("Priority 1 (critical) to 5 (backlog). Default 3.")] int priority = 3,
        [Description("JSON array of string tags, e.g. [\"core\",\"api\"].")] string? tags = null,
        [Description("Agent identity to assign this task to.")] string? assigned_to = null,
        [Description("Comma-separated task IDs this task depends on.")] string? depends_on = null,
        [Description("Parent task ID to create this as a subtask.")] int? parent_id = null)
    {
        var parsedTags = tags is not null ? JsonSerializer.Deserialize<List<string>>(tags) : null;
        var depIds = depends_on?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).ToArray();

        var task = await repo.CreateAsync(new ProjectTask
        {
            ProjectId = project_id,
            Title = title,
            Description = description,
            Priority = priority,
            Tags = parsedTags,
            AssignedTo = assigned_to,
            ParentId = parent_id
        }, depIds);

        return JsonSerializer.Serialize(task, JsonOpts.Default);
    }

    [McpServerTool(Name = "update_task"), Description("Update a task's fields. Records changes in audit history.")]
    public static async Task<string> UpdateTask(
        ITaskRepository repo,
        IDispatchDetectionService detection,
        [Description("Task ID to update.")] int task_id,
        [Description("Your agent identity (required for audit trail).")] string agent,
        [Description("New title.")] string? title = null,
        [Description("New description.")] string? description = null,
        [Description("New status: planned, in_progress, review, blocked, done, cancelled.")] string? status = null,
        [Description("New priority 1-5.")] int? priority = null,
        [Description("New assigned agent.")] string? assigned_to = null,
        [Description("JSON array of string tags.")] string? tags = null,
        [Description("New parent task ID.")] int? parent_id = null)
    {
        var current = await repo.GetByIdAsync(task_id);
        var oldStatus = current?.Status.ToDbValue();

        var changes = new Dictionary<string, object?>();
        if (title is not null) changes["title"] = title;
        if (description is not null) changes["description"] = description;
        if (status is not null) changes["status"] = EnumExtensions.ParseTaskStatus(status);
        if (priority is not null) changes["priority"] = priority.Value;
        if (assigned_to is not null) changes["assigned_to"] = assigned_to;
        if (tags is not null) changes["tags"] = JsonSerializer.Deserialize<List<string>>(tags);
        if (parent_id is not null) changes["parent_id"] = parent_id.Value;

        var updated = await repo.UpdateAsync(task_id, changes, agent);

        if (status is not null && status != oldStatus)
            await detection.OnTaskStatusChangedAsync(updated, oldStatus!, status, agent);

        return JsonSerializer.Serialize(updated, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_task"), Description("Get full task details including dependencies, subtasks, and recent messages.")]
    public static async Task<string> GetTask(
        ITaskRepository repo,
        [Description("Task ID.")] int task_id)
    {
        var detail = await repo.GetDetailAsync(task_id);
        return JsonSerializer.Serialize(detail, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_tasks"), Description("List tasks in a project with optional filters. Returns summaries without descriptions.")]
    public static async Task<string> ListTasks(
        ITaskRepository repo,
        [Description("Project ID.")] string project_id,
        [Description("Filter by statuses (comma-separated): planned,in_progress,review,blocked,done,cancelled.")] string? status = null,
        [Description("Filter by assigned agent.")] string? assigned_to = null,
        [Description("Filter by tags (comma-separated). Task must have ALL specified tags.")] string? tags = null,
        [Description("Filter: tasks at this priority or higher (lower number = higher priority).")] int? priority = null,
        [Description("Filter by parent task ID to list subtasks. Omit for top-level tasks.")] int? parent_id = null)
    {
        var statuses = status?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EnumExtensions.ParseTaskStatus).ToArray();
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tasks = await repo.ListAsync(project_id, statuses, assigned_to, tagList, priority, parent_id);
        return JsonSerializer.Serialize(tasks, JsonOpts.Default);
    }

    [McpServerTool(Name = "next_task"), Description("Get the next unblocked task to work on. Checks subtasks of in-progress parents first, then top-level planned tasks. Ranks by priority, then fewer dependencies, then lower ID.")]
    public static async Task<string> NextTask(
        ITaskRepository repo,
        [Description("Project ID.")] string project_id,
        [Description("Optionally filter to tasks assigned to this agent.")] string? assigned_to = null)
    {
        var next = await repo.GetNextTaskAsync(project_id, assigned_to);
        if (next is null)
            return JsonSerializer.Serialize(new { message = "No unblocked tasks available." }, JsonOpts.Default);
        return JsonSerializer.Serialize(next, JsonOpts.Default);
    }

    [McpServerTool(Name = "add_dependency"), Description("Add a dependency between tasks. Rejects if it would create a cycle.")]
    public static async Task<string> AddDependency(
        ITaskRepository repo,
        [Description("The task that is blocked.")] int task_id,
        [Description("The task it depends on.")] int depends_on)
    {
        await repo.AddDependencyAsync(task_id, depends_on);
        return JsonSerializer.Serialize(new { message = $"Task {task_id} now depends on task {depends_on}." }, JsonOpts.Default);
    }

    [McpServerTool(Name = "remove_dependency"), Description("Remove a dependency between tasks.")]
    public static async Task<string> RemoveDependency(
        ITaskRepository repo,
        [Description("The task that was blocked.")] int task_id,
        [Description("The task it depended on.")] int depends_on)
    {
        await repo.RemoveDependencyAsync(task_id, depends_on);
        return JsonSerializer.Serialize(new { message = $"Removed dependency: task {task_id} no longer depends on task {depends_on}." }, JsonOpts.Default);
    }
}

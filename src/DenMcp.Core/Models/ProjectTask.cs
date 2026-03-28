namespace DenMcp.Core.Models;

public sealed class ProjectTask
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Planned;
    public int Priority { get; set; } = 3;
    public string? AssignedTo { get; set; }
    public int? ParentId { get; set; }
    public List<string>? Tags { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TaskSummary
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Title { get; set; }
    public TaskStatus Status { get; set; }
    public int Priority { get; set; }
    public string? AssignedTo { get; set; }
    public int? ParentId { get; set; }
    public List<string>? Tags { get; set; }
    public int DependencyCount { get; set; }
    public int SubtaskCount { get; set; }
}

public sealed class TaskDetail
{
    public required ProjectTask Task { get; set; }
    public required List<TaskDependencyInfo> Dependencies { get; set; }
    public required List<TaskSummary> Subtasks { get; set; }
    public required List<Message> RecentMessages { get; set; }
}

public sealed class TaskDependencyInfo
{
    public int TaskId { get; set; }
    public required string Title { get; set; }
    public TaskStatus Status { get; set; }
}

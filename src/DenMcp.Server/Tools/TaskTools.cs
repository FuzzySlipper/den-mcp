using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging;
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
        [Description("Detailed description / acceptance criteria (markdown).")]
        string? description = null,
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
        ILogger<TaskTools> logger,
        [Description("Task ID to update.")] int task_id,
        [Description("Your agent identity (required for audit trail).")]
        string agent,
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
        {
            try
            {
                await detection.OnTaskStatusChangedAsync(updated, oldStatus!, status, agent);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dispatch detection failed for task {TaskId}", task_id);
            }
        }

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
        [Description("Filter by statuses (comma-separated): planned,in_progress,review,blocked,done,cancelled.")]
        string? status = null,
        [Description("Filter by assigned agent.")] string? assigned_to = null,
        [Description("Filter by tags (comma-separated). Task must have ALL specified tags.")] string? tags = null,
        [Description("Filter: tasks at this priority or higher (lower number = higher priority).")]
        int? priority = null,
        [Description("Filter by parent task ID to list subtasks. Omit for top-level tasks.")]
        int? parent_id = null)
    {
        var statuses = status?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EnumExtensions.ParseTaskStatus).ToArray();
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tasks = await repo.ListAsync(project_id, statuses, assigned_to, tagList, priority, parent_id);
        return JsonSerializer.Serialize(tasks, JsonOpts.Default);
    }

    [McpServerTool(Name = "create_review_round"), Description("Create a review round for a task with explicit branch and commit metadata.")]
    public static async Task<string> CreateReviewRound(
        IReviewRoundRepository repo,
        [Description("Task ID.")] int task_id,
        [Description("Agent or user requesting review.")] string requested_by,
        [Description("Head branch under review, e.g. task/544-fix-review-loop.")] string branch,
        [Description("Base branch for the intended diff, e.g. main or task/543-parent.")] string base_branch,
        [Description("Base commit SHA for the review diff.")] string base_commit,
        [Description("Head commit SHA being reviewed.")] string head_commit,
        [Description("Optional last reviewed head SHA. Defaults to the previous round's head when omitted.")] string? last_reviewed_head_commit = null,
        [Description("Optional number of commits since the last review round.")] int? commits_since_last_review = null,
        [Description("Optional JSON array of test commands run by the implementer.")] string? tests_run = null,
        [Description("Optional scope notes or rereview notes.")] string? notes = null,
        [Description("Optional preferred diff base ref for stacked reviews, e.g. task/543-parent. Defaults to base_branch.")] string? preferred_diff_base_ref = null,
        [Description("Optional preferred diff base commit. Defaults to base_commit.")] string? preferred_diff_base_commit = null,
        [Description("Optional preferred diff head ref. Defaults to branch.")] string? preferred_diff_head_ref = null,
        [Description("Optional preferred diff head commit. Defaults to head_commit.")] string? preferred_diff_head_commit = null,
        [Description("Optional alternate/global diff base ref, e.g. main.")] string? alternate_diff_base_ref = null,
        [Description("Optional alternate/global diff base commit.")] string? alternate_diff_base_commit = null,
        [Description("Optional alternate/global diff head ref. Defaults to branch when alternate diff is provided.")] string? alternate_diff_head_ref = null,
        [Description("Optional alternate/global diff head commit. Defaults to head_commit when alternate diff is provided.")] string? alternate_diff_head_commit = null,
        [Description("Optional explicit delta base commit. Defaults to last_reviewed_head_commit or the previous round's head.")] string? delta_base_commit = null,
        [Description("Optional count of inherited commits from unmerged parent work.")] int? inherited_commit_count = null,
        [Description("Optional count of task-local commits on top of inherited work.")] int? task_local_commit_count = null)
    {
        var parsedTests = tests_run is not null ? JsonSerializer.Deserialize<List<string>>(tests_run) : null;
        var round = await repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task_id,
            RequestedBy = requested_by,
            Branch = branch,
            BaseBranch = base_branch,
            BaseCommit = base_commit,
            HeadCommit = head_commit,
            LastReviewedHeadCommit = last_reviewed_head_commit,
            CommitsSinceLastReview = commits_since_last_review,
            TestsRun = parsedTests,
            Notes = notes,
            PreferredDiffBaseRef = preferred_diff_base_ref,
            PreferredDiffBaseCommit = preferred_diff_base_commit,
            PreferredDiffHeadRef = preferred_diff_head_ref,
            PreferredDiffHeadCommit = preferred_diff_head_commit,
            AlternateDiffBaseRef = alternate_diff_base_ref,
            AlternateDiffBaseCommit = alternate_diff_base_commit,
            AlternateDiffHeadRef = alternate_diff_head_ref,
            AlternateDiffHeadCommit = alternate_diff_head_commit,
            DeltaBaseCommit = delta_base_commit,
            InheritedCommitCount = inherited_commit_count,
            TaskLocalCommitCount = task_local_commit_count
        });
        return JsonSerializer.Serialize(round, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_review_rounds"), Description("List review rounds for a task in chronological order.")]
    public static async Task<string> ListReviewRounds(
        IReviewRoundRepository repo,
        [Description("Task ID.")] int task_id)
    {
        var rounds = await repo.ListByTaskAsync(task_id);
        return JsonSerializer.Serialize(rounds, JsonOpts.Default);
    }

    [McpServerTool(Name = "set_review_verdict"), Description("Set the verdict for a review round.")]
    public static async Task<string> SetReviewVerdict(
        IReviewRoundRepository repo,
        [Description("Review round ID.")] int review_round_id,
        [Description("Verdict: changes_requested, looks_good, follow_up_needed, blocked_by_dependency.")] string verdict,
        [Description("Agent or user setting the verdict.")] string decided_by,
        [Description("Optional verdict notes.")] string? notes = null)
    {
        var updated = await repo.SetVerdictAsync(review_round_id, EnumExtensions.ParseReviewVerdict(verdict), decided_by, notes);
        return JsonSerializer.Serialize(updated, JsonOpts.Default);
    }

    [McpServerTool(Name = "create_review_finding"), Description("Create a structured finding for a review round.")]
    public static async Task<string> CreateReviewFinding(
        IReviewFindingRepository repo,
        [Description("Review round ID.")] int review_round_id,
        [Description("Agent or reviewer creating the finding.")] string created_by,
        [Description("Category: blocking_bug, acceptance_gap, test_weakness, follow_up_candidate.")] string category,
        [Description("Short finding summary.")] string summary,
        [Description("Optional detailed reviewer notes.")] string? notes = null,
        [Description("Optional JSON array of file refs such as [\"src/Foo.cs:42\"].")] string? file_references = null,
        [Description("Optional JSON array of test commands relevant to the finding.")] string? test_commands = null)
    {
        var parsedFileRefs = file_references is not null ? JsonSerializer.Deserialize<List<string>>(file_references) : null;
        var parsedTestCommands = test_commands is not null ? JsonSerializer.Deserialize<List<string>>(test_commands) : null;
        var finding = await repo.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = review_round_id,
            CreatedBy = created_by,
            Category = EnumExtensions.ParseReviewFindingCategory(category),
            Summary = summary,
            Notes = notes,
            FileReferences = parsedFileRefs,
            TestCommands = parsedTestCommands
        });
        return JsonSerializer.Serialize(finding, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_review_findings"), Description("List review findings for a task or a specific review round.")]
    public static async Task<string> ListReviewFindings(
        IReviewFindingRepository repo,
        IReviewRoundRepository reviewRoundRepo,
        [Description("Task ID.")] int task_id,
        [Description("Optional review round ID filter.")] int? review_round_id = null,
        [Description("Optional statuses (comma-separated): open, claimed_fixed, verified_fixed, not_fixed, superseded, split_to_follow_up.")] string? status = null,
        [Description("Optional resolved filter. True = resolved/history, false = unresolved only.")] bool? resolved = null)
    {
        var statuses = EnumExtensions.GetReviewFindingStatuses(status, resolved);

        if (review_round_id is not null)
        {
            var round = await reviewRoundRepo.GetByIdAsync(review_round_id.Value);
            if (round is null || round.TaskId != task_id)
                throw new KeyNotFoundException($"Review round {review_round_id.Value} not found for task {task_id}");

            var roundFindings = await repo.ListByReviewRoundAsync(review_round_id.Value, statuses);
            return JsonSerializer.Serialize(roundFindings, JsonOpts.Default);
        }

        var findings = await repo.ListByTaskAsync(task_id, statuses);
        return JsonSerializer.Serialize(findings, JsonOpts.Default);
    }

    [McpServerTool(Name = "respond_to_review_finding"), Description("Add implementer response notes to a review finding and optionally mark it claimed_fixed or otherwise update status.")]
    public static async Task<string> RespondToReviewFinding(
        IReviewFindingRepository repo,
        ITaskRepository taskRepo,
        [Description("Review finding ID.")] int review_finding_id,
        [Description("Agent or user responding to the finding.")] string responded_by,
        [Description("Optional implementer response notes.")] string? response_notes = null,
        [Description("Optional status update: open, claimed_fixed, verified_fixed, not_fixed, superseded, split_to_follow_up.")] string? status = null,
        [Description("Optional notes explaining the status update.")] string? status_notes = null,
        [Description("Optional follow-up task ID when the finding is split out.")] int? follow_up_task_id = null)
    {
        await ValidateFollowUpTaskProjectAsync(repo, taskRepo, review_finding_id, follow_up_task_id);

        var updated = await repo.RespondAsync(review_finding_id, new RespondToReviewFindingInput
        {
            RespondedBy = responded_by,
            ResponseNotes = response_notes,
            Status = status is not null ? EnumExtensions.ParseReviewFindingStatus(status) : null,
            StatusNotes = status_notes,
            FollowUpTaskId = follow_up_task_id
        });
        return JsonSerializer.Serialize(updated, JsonOpts.Default);
    }

    [McpServerTool(Name = "set_review_finding_status"), Description("Update the status for a review finding.")]
    public static async Task<string> SetReviewFindingStatus(
        IReviewFindingRepository repo,
        ITaskRepository taskRepo,
        [Description("Review finding ID.")] int review_finding_id,
        [Description("New status: open, claimed_fixed, verified_fixed, not_fixed, superseded, split_to_follow_up.")] string status,
        [Description("Agent or user updating the finding status.")] string updated_by,
        [Description("Optional status notes.")] string? notes = null,
        [Description("Optional follow-up task ID when the finding is split out.")] int? follow_up_task_id = null)
    {
        await ValidateFollowUpTaskProjectAsync(repo, taskRepo, review_finding_id, follow_up_task_id);

        var updated = await repo.SetStatusAsync(review_finding_id, new UpdateReviewFindingStatusInput
        {
            Status = EnumExtensions.ParseReviewFindingStatus(status),
            UpdatedBy = updated_by,
            Notes = notes,
            FollowUpTaskId = follow_up_task_id
        });
        return JsonSerializer.Serialize(updated, JsonOpts.Default);
    }

    [McpServerTool(Name = "request_review"), Description("Create a review round and post a standardized review request or rereview packet to the task thread.")]
    public static async Task<string> RequestReview(
        IReviewWorkflowService workflow,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Agent or user requesting review.")] string requested_by,
        [Description("Head branch under review, e.g. task/597-review-packet-ux.")] string branch,
        [Description("Base branch for the intended diff, e.g. main or task/596-parent.")] string base_branch,
        [Description("Base commit SHA for the review diff.")] string base_commit,
        [Description("Head commit SHA being reviewed.")] string head_commit,
        [Description("Optional last reviewed head SHA. Defaults to the previous round's head when omitted.")] string? last_reviewed_head_commit = null,
        [Description("Optional number of commits since the last review round.")] int? commits_since_last_review = null,
        [Description("Optional JSON array of test commands run by the implementer.")] string? tests_run = null,
        [Description("Optional scope notes or rereview notes.")] string? notes = null,
        [Description("Optional preferred diff base ref for stacked reviews, e.g. task/596-parent. Defaults to base_branch.")] string? preferred_diff_base_ref = null,
        [Description("Optional preferred diff base commit. Defaults to base_commit.")] string? preferred_diff_base_commit = null,
        [Description("Optional preferred diff head ref. Defaults to branch.")] string? preferred_diff_head_ref = null,
        [Description("Optional preferred diff head commit. Defaults to head_commit.")] string? preferred_diff_head_commit = null,
        [Description("Optional alternate/global diff base ref, e.g. main.")] string? alternate_diff_base_ref = null,
        [Description("Optional alternate/global diff base commit.")] string? alternate_diff_base_commit = null,
        [Description("Optional alternate/global diff head ref. Defaults to branch when alternate diff is provided.")] string? alternate_diff_head_ref = null,
        [Description("Optional alternate/global diff head commit. Defaults to head_commit when alternate diff is provided.")] string? alternate_diff_head_commit = null,
        [Description("Optional explicit delta base commit. Defaults to last_reviewed_head_commit or the previous round's head.")] string? delta_base_commit = null,
        [Description("Optional count of inherited commits from unmerged parent work.")] int? inherited_commit_count = null,
        [Description("Optional count of task-local commits on top of inherited work.")] int? task_local_commit_count = null,
        [Description("Optional task-thread message to reply to.")] int? thread_id = null)
    {
        var parsedTests = tests_run is not null ? JsonSerializer.Deserialize<List<string>>(tests_run) : null;
        var result = await workflow.RequestReviewAsync(project_id, new RequestReviewInput
        {
            TaskId = task_id,
            RequestedBy = requested_by,
            Branch = branch,
            BaseBranch = base_branch,
            BaseCommit = base_commit,
            HeadCommit = head_commit,
            LastReviewedHeadCommit = last_reviewed_head_commit,
            CommitsSinceLastReview = commits_since_last_review,
            TestsRun = parsedTests,
            Notes = notes,
            PreferredDiffBaseRef = preferred_diff_base_ref,
            PreferredDiffBaseCommit = preferred_diff_base_commit,
            PreferredDiffHeadRef = preferred_diff_head_ref,
            PreferredDiffHeadCommit = preferred_diff_head_commit,
            AlternateDiffBaseRef = alternate_diff_base_ref,
            AlternateDiffBaseCommit = alternate_diff_base_commit,
            AlternateDiffHeadRef = alternate_diff_head_ref,
            AlternateDiffHeadCommit = alternate_diff_head_commit,
            DeltaBaseCommit = delta_base_commit,
            InheritedCommitCount = inherited_commit_count,
            TaskLocalCommitCount = task_local_commit_count,
            ThreadId = thread_id
        });
        return JsonSerializer.Serialize(result, JsonOpts.Default);
    }

    [McpServerTool(Name = "post_review_findings"), Description("Post a standardized reviewer findings packet for a review round back to the task thread.")]
    public static async Task<string> PostReviewFindings(
        IReviewWorkflowService workflow,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Review round ID.")] int review_round_id,
        [Description("Agent or user posting the findings packet.")] string sender,
        [Description("Optional task-thread message to reply to.")] int? thread_id = null,
        [Description("Optional summary note to append to the packet.")] string? notes = null)
    {
        var result = await workflow.PostReviewFindingsAsync(project_id, new PostReviewFindingsInput
        {
            TaskId = task_id,
            ReviewRoundId = review_round_id,
            Sender = sender,
            ThreadId = thread_id,
            Notes = notes
        });
        return JsonSerializer.Serialize(result, JsonOpts.Default);
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

    private static async Task ValidateFollowUpTaskProjectAsync(
        IReviewFindingRepository findingRepo,
        ITaskRepository taskRepo,
        int reviewFindingId,
        int? followUpTaskId)
    {
        if (followUpTaskId is null)
            return;

        var finding = await findingRepo.GetByIdAsync(reviewFindingId)
            ?? throw new KeyNotFoundException($"Review finding {reviewFindingId} not found");
        var findingTask = await taskRepo.GetByIdAsync(finding.TaskId)
            ?? throw new KeyNotFoundException($"Owning task {finding.TaskId} not found for review finding {reviewFindingId}");
        var followUpTask = await taskRepo.GetByIdAsync(followUpTaskId.Value)
            ?? throw new KeyNotFoundException($"Follow-up task {followUpTaskId.Value} not found");

        if (!string.Equals(findingTask.ProjectId, followUpTask.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Follow-up task {followUpTaskId.Value} must be in the same project as review finding {reviewFindingId}.");
        }
    }
}

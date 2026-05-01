using System.Text;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Services;

public interface IReviewFindingTriageService
{
    Task<SplitFindingsToFollowUpResult> SplitFindingsToFollowUpAsync(SplitFindingsToFollowUpInput input);
}

public sealed class SplitFindingsToFollowUpInput
{
    public required int TaskId { get; set; }
    public required string ProjectId { get; set; }
    public required List<int> FindingIds { get; set; }
    public required string SplitBy { get; set; }
    public string? FollowUpTitle { get; set; }
    public int? FollowUpParentTaskId { get; set; }
    public int? FollowUpPriority { get; set; }
    public string? FollowUpAssignedTo { get; set; }
    public List<string>? FollowUpTags { get; set; }
    public bool OverrideBlocking { get; set; }
}

public sealed class SplitFindingsToFollowUpResult
{
    public required ProjectTask FollowUpTask { get; set; }
    public required List<ReviewFinding> UpdatedFindings { get; set; }
    public required List<int> SkippedFindingIds { get; set; }
}

public sealed class ReviewFindingTriageService : IReviewFindingTriageService
{
    private readonly ITaskRepository _tasks;
    private readonly IReviewFindingRepository _findings;
    private readonly ILogger<ReviewFindingTriageService> _logger;

    public ReviewFindingTriageService(
        ITaskRepository tasks,
        IReviewFindingRepository findings,
        ILogger<ReviewFindingTriageService> logger)
    {
        _tasks = tasks;
        _findings = findings;
        _logger = logger;
    }

    public async Task<SplitFindingsToFollowUpResult> SplitFindingsToFollowUpAsync(SplitFindingsToFollowUpInput input)
    {
        if (input.FindingIds is not { Count: > 0 })
            throw new ArgumentException("At least one finding ID is required.", nameof(input));

        // Load and validate the source task
        var sourceTask = await _tasks.GetByIdAsync(input.TaskId)
            ?? throw new KeyNotFoundException($"Task {input.TaskId} not found");

        if (!string.Equals(sourceTask.ProjectId, input.ProjectId, StringComparison.Ordinal))
            throw new KeyNotFoundException($"Task {input.TaskId} not found in project {input.ProjectId}");

        // Validate follow-up parent if specified
        if (input.FollowUpParentTaskId is not null)
        {
            var parentTask = await _tasks.GetByIdAsync(input.FollowUpParentTaskId.Value)
                ?? throw new KeyNotFoundException($"Parent task {input.FollowUpParentTaskId.Value} not found");

            if (!string.Equals(parentTask.ProjectId, input.ProjectId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Parent task {input.FollowUpParentTaskId.Value} must be in the same project.");
        }

        // Load findings, validate ownership and blocking status
        var loadedFindings = new List<ReviewFinding>();
        var skippedIds = new List<int>();

        foreach (var findingId in input.FindingIds)
        {
            var finding = await _findings.GetByIdAsync(findingId);
            if (finding is null)
                throw new KeyNotFoundException($"Review finding {findingId} not found");

            if (finding.TaskId != input.TaskId)
                throw new InvalidOperationException(
                    $"Review finding {findingId} does not belong to task {input.TaskId}");

            if (IsBlockingCategory(finding.Category) && !input.OverrideBlocking)
            {
                _logger.LogInformation(
                    "Skipping blocking finding {FindingId} ({Category}). Set override_blocking=true to include.",
                    finding.Id, finding.Category);
                skippedIds.Add(finding.Id);
                continue;
            }

            loadedFindings.Add(finding);
        }

        if (loadedFindings.Count == 0)
            throw new InvalidOperationException(
                "No findings to split. All findings were either blocking (set override_blocking=true to include) or already resolved.");

        // Create the follow-up task with generated description
        var description = BuildFollowUpDescription(loadedFindings);
        var title = input.FollowUpTitle ?? $"Follow up: {loadedFindings.Count} review finding(s) from #{input.TaskId}";

        var followUpTask = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = input.ProjectId,
            Title = title,
            Description = description,
            Priority = input.FollowUpPriority ?? 3,
            AssignedTo = input.FollowUpAssignedTo,
            ParentId = input.FollowUpParentTaskId,
            Tags = input.FollowUpTags
        });

        // Update each finding status to split_to_follow_up with the new task ID
        var updatedFindings = new List<ReviewFinding>();
        foreach (var finding in loadedFindings)
        {
            var updated = await _findings.SetStatusAsync(finding.Id, new UpdateReviewFindingStatusInput
            {
                Status = ReviewFindingStatus.SplitToFollowUp,
                UpdatedBy = input.SplitBy,
                Notes = $"Split to follow-up task #{followUpTask.Id}",
                FollowUpTaskId = followUpTask.Id
            });
            updatedFindings.Add(updated);
        }

        _logger.LogInformation(
            "Split {Count} findings from task #{TaskId} to follow-up task #{FollowUpTaskId}. Skipped: {SkippedCount}",
            updatedFindings.Count, input.TaskId, followUpTask.Id, skippedIds.Count);

        return new SplitFindingsToFollowUpResult
        {
            FollowUpTask = followUpTask,
            UpdatedFindings = updatedFindings,
            SkippedFindingIds = skippedIds
        };
    }

    private static bool IsBlockingCategory(ReviewFindingCategory category) =>
        category == ReviewFindingCategory.BlockingBug;

    internal static string BuildFollowUpDescription(List<ReviewFinding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Follow-up task for review findings split from the parent task.");
        sb.AppendLine();
        sb.AppendLine("## Findings");
        sb.AppendLine();

        foreach (var finding in findings.OrderBy(f => f.FindingNumber))
        {
            sb.AppendLine($"### {finding.FindingKey} — {finding.Category.ToDbValue()}");
            sb.AppendLine();
            sb.AppendLine($"**Summary**: {finding.Summary}");
            sb.AppendLine($"**Finding ID**: {finding.Id}");
            sb.AppendLine($"**Status at split**: {finding.Status.ToDbValue()}");
            sb.AppendLine($"**Review round**: {finding.ReviewRoundNumber}");

            if (!string.IsNullOrWhiteSpace(finding.Notes))
            {
                sb.AppendLine();
                sb.AppendLine($"**Notes**: {finding.Notes}");
            }

            if (finding.FileReferences is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine($"**File references**: {string.Join(", ", finding.FileReferences.Select(f => $"`{f}`"))}");
            }

            if (finding.TestCommands is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine($"**Test commands**: {string.Join(", ", finding.TestCommands.Select(t => $"`{t}`"))}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Acceptance criteria");
        sb.AppendLine();
        sb.AppendLine("Each finding listed above should be addressed (fixed, accepted, or further deferred).");
        sb.AppendLine("Close this task once all findings are resolved or explicitly accepted.");

        return sb.ToString().TrimEnd();
    }
}

using System.Text;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface IReviewWorkflowService
{
    Task<ReviewPacketResult> RequestReviewAsync(string projectId, RequestReviewInput input);
    Task<ReviewPacketResult> PostReviewFindingsAsync(string projectId, PostReviewFindingsInput input);
}

public sealed class ReviewWorkflowService : IReviewWorkflowService
{
    private readonly ITaskRepository _tasks;
    private readonly IReviewRoundRepository _reviewRounds;
    private readonly IReviewFindingRepository _reviewFindings;
    private readonly IMessageRepository _messages;

    public ReviewWorkflowService(
        ITaskRepository tasks,
        IReviewRoundRepository reviewRounds,
        IReviewFindingRepository reviewFindings,
        IMessageRepository messages)
    {
        _tasks = tasks;
        _reviewRounds = reviewRounds;
        _reviewFindings = reviewFindings;
        _messages = messages;
    }

    public async Task<ReviewPacketResult> RequestReviewAsync(string projectId, RequestReviewInput input)
    {
        var detail = await _tasks.GetDetailAsync(input.TaskId);
        ValidateTaskProject(detail, projectId, input.TaskId);
        await ValidateThreadAsync(projectId, input.TaskId, input.ThreadId);

        var round = await _reviewRounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = input.TaskId,
            RequestedBy = input.RequestedBy,
            Branch = input.Branch,
            BaseBranch = input.BaseBranch,
            BaseCommit = input.BaseCommit,
            HeadCommit = input.HeadCommit,
            LastReviewedHeadCommit = input.LastReviewedHeadCommit,
            CommitsSinceLastReview = input.CommitsSinceLastReview,
            TestsRun = input.TestsRun,
            Notes = input.Notes,
            PreferredDiffBaseRef = input.PreferredDiffBaseRef,
            PreferredDiffBaseCommit = input.PreferredDiffBaseCommit,
            PreferredDiffHeadRef = input.PreferredDiffHeadRef,
            PreferredDiffHeadCommit = input.PreferredDiffHeadCommit,
            AlternateDiffBaseRef = input.AlternateDiffBaseRef,
            AlternateDiffBaseCommit = input.AlternateDiffBaseCommit,
            AlternateDiffHeadRef = input.AlternateDiffHeadRef,
            AlternateDiffHeadCommit = input.AlternateDiffHeadCommit,
            DeltaBaseCommit = input.DeltaBaseCommit,
            InheritedCommitCount = input.InheritedCommitCount,
            TaskLocalCommitCount = input.TaskLocalCommitCount
        });

        var addressedFindingDetails = detail.OpenReviewFindings
            .Concat(detail.ResolvedReviewFindings)
            .Where(finding => finding.ResponseAt is not null || finding.Status != ReviewFindingStatus.Open)
            .OrderBy(finding => finding.FindingNumber)
            .ToList();
        var openFindingDetails = detail.OpenReviewFindings
            .OrderBy(finding => finding.FindingNumber)
            .ToList();
        var findingsAddressed = GetAddressedFindings(detail);
        var openFindings = openFindingDetails.Select(FormatFindingOverviewLine).ToList();

        var packet = BuildReviewRequestPacket(round, input.Notes, findingsAddressed, openFindings);
        var message = await _messages.CreateAsync(new Message
        {
            ProjectId = projectId,
            TaskId = input.TaskId,
            ThreadId = input.ThreadId,
            Sender = input.RequestedBy,
            Content = packet.Content,
            Metadata = JsonSerializer.SerializeToElement(new
            {
                type = packet.Kind == ReviewPacketKind.RereviewRequest ? "rereview_packet" : "review_request_packet",
                review_round_id = round.Id,
                review_round_number = round.RoundNumber,
                branch = round.Branch,
                base_branch = round.BaseBranch,
                base_commit = round.BaseCommit,
                head_commit = round.HeadCommit,
                last_reviewed_head_commit = round.LastReviewedHeadCommit,
                commits_since_last_review = round.CommitsSinceLastReview,
                preferred_diff = new
                {
                    base_ref = round.PreferredDiff.BaseRef,
                    base_commit = round.PreferredDiff.BaseCommit,
                    head_ref = round.PreferredDiff.HeadRef,
                    head_commit = round.PreferredDiff.HeadCommit
                },
                alternate_diff = round.AlternateDiff is null ? null : new
                {
                    base_ref = round.AlternateDiff.BaseRef,
                    base_commit = round.AlternateDiff.BaseCommit,
                    head_ref = round.AlternateDiff.HeadRef,
                    head_commit = round.AlternateDiff.HeadCommit
                },
                delta_base_commit = round.DeltaDiff?.BaseCommit,
                findings_addressed = addressedFindingDetails.Select(ToMetadataFinding).ToList(),
                open_findings = openFindingDetails.Select(ToMetadataFinding).ToList(),
                tests_run = round.TestsRun
            })
        });

        return new ReviewPacketResult
        {
            ReviewRound = round,
            Message = message,
            Packet = packet,
            FindingsAddressed = findingsAddressed,
            OpenFindings = openFindings,
            TestCommands = round.TestsRun?.ToList() ?? []
        };
    }

    public async Task<ReviewPacketResult> PostReviewFindingsAsync(string projectId, PostReviewFindingsInput input)
    {
        var detail = await _tasks.GetDetailAsync(input.TaskId);
        ValidateTaskProject(detail, projectId, input.TaskId);
        await ValidateThreadAsync(projectId, input.TaskId, input.ThreadId);

        var round = detail.ReviewRounds.FirstOrDefault(reviewRound => reviewRound.Id == input.ReviewRoundId);
        if (round is null)
            throw new KeyNotFoundException($"Review round {input.ReviewRoundId} not found for task {input.TaskId}");

        var findings = await _reviewFindings.ListByReviewRoundAsync(input.ReviewRoundId);
        var openFindingDetails = detail.OpenReviewFindings
            .OrderBy(finding => finding.FindingNumber)
            .ToList();
        var openFindings = openFindingDetails.Select(FormatFindingOverviewLine).ToList();
        var packet = BuildReviewFindingsPacket(round, findings, openFindings, input.Notes);
        var reviewerCommands = findings
            .SelectMany(finding => finding.TestCommands ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var message = await _messages.CreateAsync(new Message
        {
            ProjectId = projectId,
            TaskId = input.TaskId,
            ThreadId = input.ThreadId,
            Sender = input.Sender,
            Content = packet.Content,
            Metadata = JsonSerializer.SerializeToElement(new
            {
                type = "review_findings_packet",
                review_round_id = round.Id,
                review_round_number = round.RoundNumber,
                branch = round.Branch,
                verdict = round.Verdict?.ToDbValue(),
                findings = findings
                    .OrderBy(finding => finding.FindingNumber)
                    .Select(ToMetadataFinding)
                    .ToList(),
                open_findings = openFindingDetails.Select(ToMetadataFinding).ToList(),
                reviewer_test_commands = reviewerCommands
            })
        });

        return new ReviewPacketResult
        {
            ReviewRound = round,
            Message = message,
            Packet = packet,
            FindingsAddressed = findings
                .OrderBy(finding => finding.FindingNumber)
                .Select(FormatFindingOverviewLine)
                .ToList(),
            OpenFindings = openFindings,
            TestCommands = reviewerCommands
        };
    }

    private static void ValidateTaskProject(TaskDetail detail, string projectId, int taskId)
    {
        if (!string.Equals(detail.Task.ProjectId, projectId, StringComparison.Ordinal))
            throw new KeyNotFoundException($"Task {taskId} not found in project {projectId}");
    }

    private async Task ValidateThreadAsync(string projectId, int taskId, int? threadId)
    {
        if (threadId is null)
            return;

        var thread = await _messages.GetThreadAsync(threadId.Value);
        if (!string.Equals(thread.Root.ProjectId, projectId, StringComparison.Ordinal) ||
            thread.Root.TaskId != taskId)
        {
            throw new InvalidOperationException(
                $"Thread {threadId.Value} does not belong to task {taskId} in project {projectId}.");
        }
    }

    private static List<string> GetAddressedFindings(TaskDetail detail)
    {
        return detail.OpenReviewFindings
            .Concat(detail.ResolvedReviewFindings)
            .Where(finding => finding.ResponseAt is not null || finding.Status != ReviewFindingStatus.Open)
            .OrderBy(finding => finding.FindingNumber)
            .Select(FormatFindingOverviewLine)
            .ToList();
    }

    private static ReviewPacket BuildReviewRequestPacket(
        ReviewRound round,
        string? notes,
        IReadOnlyList<string> findingsAddressed,
        IReadOnlyList<string> openFindings)
    {
        var isRereview = round.RoundNumber > 1 || round.LastReviewedHeadCommit is not null;
        var title = isRereview ? "Ready for rereview" : "Review request";
        var sb = new StringBuilder();

        sb.AppendLine(title);
        sb.AppendLine();
        sb.AppendLine($"Review round: `{round.RoundNumber}`");
        sb.AppendLine($"Reviewed diff: `{round.PreferredDiff.BaseRef}...{round.PreferredDiff.HeadRef}`");
        if (round.AlternateDiff is not null)
            sb.AppendLine($"Alternate diff: `{round.AlternateDiff.BaseRef}...{round.AlternateDiff.HeadRef}`");
        if (round.DeltaDiff?.BaseCommit is not null)
            sb.AppendLine($"Delta since last review: `{round.DeltaDiff.BaseCommit}..{round.HeadCommit}`");
        sb.AppendLine($"Base SHA: `{round.BaseCommit}`");
        if (round.LastReviewedHeadCommit is not null)
            sb.AppendLine($"Last reviewed SHA: `{round.LastReviewedHeadCommit}`");
        sb.AppendLine($"Current SHA: `{round.HeadCommit}`");
        if (round.CommitsSinceLastReview is not null)
            sb.AppendLine($"New commits: `{round.CommitsSinceLastReview.Value}`");

        var branchComposition = FormatBranchComposition(round);
        if (branchComposition is not null)
            sb.AppendLine($"Branch composition: {branchComposition}");

        AppendListSection(sb, "Findings addressed", findingsAddressed, skipIfEmpty: !isRereview);
        AppendListSection(sb, "Open findings", openFindings, skipIfEmpty: !isRereview);
        AppendListSection(sb, "Tests run by implementer", round.TestsRun ?? [], skipIfEmpty: false);
        AppendOptionalNotes(sb, notes ?? round.Notes);

        return new ReviewPacket
        {
            Kind = isRereview ? ReviewPacketKind.RereviewRequest : ReviewPacketKind.ReviewRequest,
            Title = title,
            Content = sb.ToString().TrimEnd()
        };
    }

    private static ReviewPacket BuildReviewFindingsPacket(
        ReviewRound round,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<string> openFindings,
        string? notes)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Review findings");
        sb.AppendLine();
        sb.AppendLine($"Review round: `{round.RoundNumber}`");
        sb.AppendLine($"Verdict: `{round.Verdict?.ToDbValue() ?? "pending"}`");
        sb.AppendLine($"Reviewed diff: `{round.PreferredDiff.BaseRef}...{round.PreferredDiff.HeadRef}`");
        if (round.AlternateDiff is not null)
            sb.AppendLine($"Alternate diff: `{round.AlternateDiff.BaseRef}...{round.AlternateDiff.HeadRef}`");
        if (round.DeltaDiff?.BaseCommit is not null)
            sb.AppendLine($"Delta since last review: `{round.DeltaDiff.BaseCommit}..{round.HeadCommit}`");

        var reviewerCommands = findings
            .SelectMany(finding => finding.TestCommands ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        AppendListSection(sb, "Reviewer test commands", reviewerCommands, skipIfEmpty: false);

        sb.AppendLine("Findings:");
        if (findings.Count == 0)
        {
            sb.AppendLine("- (none)");
        }
        else
        {
            foreach (var finding in findings.OrderBy(finding => finding.FindingNumber))
            {
                sb.AppendLine();
                sb.AppendLine($"{finding.FindingKey} - {finding.Category.ToDbValue()}");
                sb.AppendLine($"Status: {finding.Status.ToDbValue()}");
                sb.AppendLine($"Summary: {finding.Summary}");
                if (finding.FileReferences is { Count: > 0 })
                    sb.AppendLine($"Files: {string.Join(", ", finding.FileReferences.Select(value => $"`{value}`"))}");
                if (finding.TestCommands is { Count: > 0 })
                    sb.AppendLine($"Tests: {string.Join(", ", finding.TestCommands.Select(value => $"`{value}`"))}");
                if (!string.IsNullOrWhiteSpace(finding.Notes))
                    sb.AppendLine($"Notes: {CollapseWhitespace(finding.Notes)}");
            }
        }

        AppendListSection(sb, "Open findings after review", openFindings, skipIfEmpty: false);
        AppendOptionalNotes(sb, notes);

        return new ReviewPacket
        {
            Kind = ReviewPacketKind.ReviewFindings,
            Title = "Review findings",
            Content = sb.ToString().TrimEnd()
        };
    }

    private static string FormatFindingOverviewLine(ReviewFinding finding)
    {
        var line = $"`{finding.FindingKey}` `{finding.Category.ToDbValue()}` `{finding.Status.ToDbValue()}` {finding.Summary}";
        var detail = finding.StatusNotes ?? finding.ResponseNotes;
        return string.IsNullOrWhiteSpace(detail)
            ? line
            : $"{line} ({CollapseWhitespace(detail)})";
    }

    private static string? FormatBranchComposition(ReviewRound round)
    {
        var parts = new List<string>();
        if (round.InheritedCommitCount is not null)
            parts.Add($"`{round.InheritedCommitCount.Value}` inherited");
        if (round.TaskLocalCommitCount is not null)
            parts.Add($"`{round.TaskLocalCommitCount.Value}` task-local");

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static void AppendListSection(
        StringBuilder sb,
        string heading,
        IReadOnlyCollection<string> items,
        bool skipIfEmpty)
    {
        if (skipIfEmpty && items.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine($"{heading}:");
        if (items.Count == 0)
        {
            sb.AppendLine("- (none recorded)");
            return;
        }

        foreach (var item in items)
            sb.AppendLine($"- {item}");
    }

    private static void AppendOptionalNotes(StringBuilder sb, string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return;

        sb.AppendLine();
        sb.AppendLine("Notes:");
        sb.AppendLine($"- {CollapseWhitespace(notes)}");
    }

    private static string CollapseWhitespace(string value) =>
        value.ReplaceLineEndings(" ").Trim();

    private static object ToMetadataFinding(ReviewFinding finding) => new
    {
        finding_key = finding.FindingKey,
        review_round_number = finding.ReviewRoundNumber,
        category = finding.Category.ToDbValue(),
        status = finding.Status.ToDbValue(),
        summary = finding.Summary,
        file_references = finding.FileReferences,
        test_commands = finding.TestCommands,
        follow_up_task_id = finding.FollowUpTaskId
    };
}

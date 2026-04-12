namespace DenMcp.Core.Models;

public sealed class ReviewRound
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int RoundNumber { get; set; }
    public required string RequestedBy { get; set; }
    public required string Branch { get; set; }
    public required string BaseBranch { get; set; }
    public required string BaseCommit { get; set; }
    public required string HeadCommit { get; set; }
    public string? LastReviewedHeadCommit { get; set; }
    public int? CommitsSinceLastReview { get; set; }
    public List<string>? TestsRun { get; set; }
    public string? Notes { get; set; }
    public ReviewVerdict? Verdict { get; set; }
    public string? VerdictBy { get; set; }
    public string? VerdictNotes { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? VerdictAt { get; set; }
}

public sealed class CreateReviewRoundInput
{
    public int TaskId { get; set; }
    public required string RequestedBy { get; set; }
    public required string Branch { get; set; }
    public required string BaseBranch { get; set; }
    public required string BaseCommit { get; set; }
    public required string HeadCommit { get; set; }
    public string? LastReviewedHeadCommit { get; set; }
    public int? CommitsSinceLastReview { get; set; }
    public List<string>? TestsRun { get; set; }
    public string? Notes { get; set; }
}

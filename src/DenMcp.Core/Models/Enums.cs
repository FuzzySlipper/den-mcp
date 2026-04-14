namespace DenMcp.Core.Models;

public enum TaskStatus
{
    Planned,
    InProgress,
    Review,
    Blocked,
    Done,
    Cancelled
}

public enum DocType
{
    Prd,
    Spec,
    Adr,
    Convention,
    Reference,
    Note
}

public enum MessageIntent
{
    General,
    Note,
    StatusUpdate,
    Question,
    Answer,
    Handoff,
    ReviewRequest,
    ReviewFeedback,
    ReviewApproval,
    TaskReady,
    TaskBlocked
}

public enum AgentSessionStatus
{
    Active,
    Inactive
}

public enum DispatchStatus
{
    Pending,
    Approved,
    Rejected,
    Completed,
    Expired
}

public enum DispatchTriggerType
{
    Message,
    TaskStatus
}

public enum ReviewVerdict
{
    ChangesRequested,
    LooksGood,
    FollowUpNeeded,
    BlockedByDependency
}

public enum ReviewFindingCategory
{
    BlockingBug,
    AcceptanceGap,
    TestWeakness,
    FollowUpCandidate
}

public enum ReviewFindingStatus
{
    Open,
    ClaimedFixed,
    VerifiedFixed,
    NotFixed,
    Superseded,
    SplitToFollowUp
}

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

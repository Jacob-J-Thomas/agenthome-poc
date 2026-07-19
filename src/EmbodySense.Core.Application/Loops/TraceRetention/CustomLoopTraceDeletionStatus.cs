namespace EmbodySense.Core.Application.Loops.TraceRetention;

public enum CustomLoopTraceDeletionStatus
{
    Unknown = 0,
    Deleted = 1,
    Replayed = 2,
    NotFound = 3,
    Nonterminal = 4,
    HashMismatch = 5,
    Conflict = 6,
    LimitExceeded = 7,
    Invalid = 8,
    AuditUnavailable = 9,
    CommittedWithAuditWarning = 10
}

namespace EmbodySense.Core.Application.Loops.Authoring;

public enum CustomLoopAuthoringStatus
{
    Unknown = 0,
    Created = 1,
    Updated = 2,
    Deleted = 3,
    Replayed = 4,
    Invalid = 5,
    Conflict = 6,
    NotFound = 7,
    LimitExceeded = 8,
    AuditUnavailable = 9,
    CommittedWithAuditWarning = 10,
    ActiveRunExists = 11
}

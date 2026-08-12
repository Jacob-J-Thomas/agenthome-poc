namespace EmbodySense.Core.Application.Loops.Sequential;

internal enum OperationResolutionStatus
{
    Ready,
    Conflict,
    NotFound,
    LimitExceeded,
    AuditUnavailable,
    Invalid,
    Unavailable,
}

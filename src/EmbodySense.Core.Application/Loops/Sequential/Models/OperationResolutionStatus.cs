namespace EmbodySense.Core.Application.Loops.Sequential.Models;

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

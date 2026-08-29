namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

internal enum HumanInputResponseContinuationRunUpdateStatus
{
    Updated = 1,
    Reconciled = 2,
    Conflict = 3,
    NotFound = 4,
    Invalid = 5,
    Unavailable = 6,
}

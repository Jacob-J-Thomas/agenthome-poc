namespace EmbodySense.Core.Application.Loops.Retry.Models;

internal enum RunMutationStatus
{
    Committed,
    Replayed,
    NotFound,
    Conflict,
    Unavailable,
    Ambiguous,
}

namespace EmbodySense.Core.Application.Loops.Wait.Models;

internal enum RunMutationStatus
{
    Committed,
    Replayed,
    NotFound,
    Conflict,
    Unavailable,
    Ambiguous,
}

namespace EmbodySense.Core.Application.Loops.Wait;

internal enum RunMutationStatus
{
    Committed,
    Replayed,
    NotFound,
    Conflict,
    Unavailable,
    Ambiguous,
}

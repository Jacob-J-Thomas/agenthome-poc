using EmbodySense.Core.Application.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

internal sealed record CommitMapping(bool RetryStoreConflict, GovernedLoopRevisionLifecycleMutationResult? Result)
{
    internal static CommitMapping Retry { get; } = new(true, null);
    internal static CommitMapping Final(GovernedLoopRevisionLifecycleMutationResult result) => new(false, result);
}

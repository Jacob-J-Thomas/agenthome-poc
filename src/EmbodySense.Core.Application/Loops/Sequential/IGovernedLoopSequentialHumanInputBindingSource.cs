using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Rehydrates the exact selected Human Input value for one terminal checkpoint without persisting that private value in ordered-run evidence.</summary>
public interface IGovernedLoopSequentialHumanInputBindingSource
{
    /// <summary>Reads and validates one checkpoint-bound selected response projection.</summary>
    /// <param name="checkpoint">The exact retained terminal Human Input checkpoint.</param>
    /// <param name="cancellationToken">A token that cancels the source read before it completes.</param>
    /// <returns>A ready ephemeral binding, retryable unavailability, or conclusive invalid/divergent evidence.</returns>
    /// <remarks>Implementations must reject unsupported multi-value policies rather than choosing, merging, or synthesizing an output value.</remarks>
    Task<GovernedLoopSequentialHumanInputBindingReadResult> ResolveAsync(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        CancellationToken cancellationToken = default);
}

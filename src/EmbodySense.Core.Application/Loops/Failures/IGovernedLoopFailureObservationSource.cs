using EmbodySense.Core.Application.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Loops.Failures;

/// <summary>Reports bounded failure observations without throwing implementation exceptions across the runtime boundary.</summary>
public interface IGovernedLoopFailureObservationSource
{
    /// <summary>Reads observations for one exact node attempt.</summary>
    /// <param name="context">The exact classification coordinates.</param>
    /// <param name="cancellationToken">Cancels the read before evidence is retained.</param>
    /// <returns>A bounded observation set, or null when the source cannot provide trustworthy evidence.</returns>
    Task<IReadOnlyList<GovernedLoopFailureObservation>?> ReadAsync(GovernedLoopFailureClassificationContext context, CancellationToken cancellationToken = default);
}

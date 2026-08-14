using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Loads the immutable canonical admission and invocation hand-off retained for an ordered runtime run.</summary>
public interface IGovernedLoopSequentialRunEvidenceSource
{
    /// <summary>Resolves exact retained evidence without admitting again or consulting mutable graph, role, or grant heads.</summary>
    Task<GovernedLoopSequentialRunEvidence?> ResolveAsync(
        string runId,
        CancellationToken cancellationToken = default);
}

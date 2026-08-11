using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Authenticates ordered-runtime outcome events and idempotently retains their canonical evidence projection.</summary>
public interface IGovernedLoopSequentialOrderedNodeEvidenceRecorder : IGovernedLoopSequentialNodeEvidenceSource
{
    /// <summary>
    /// Authenticates the exact durable ordered event and commits or replays its canonical evidence identity without dispatching node behavior.
    /// </summary>
    Task<GovernedLoopSequentialNodeHandlerResult> RetainAsync(
        GovernedLoopSequentialOrderedNodeEvidenceRequest request,
        CancellationToken cancellationToken = default);
}

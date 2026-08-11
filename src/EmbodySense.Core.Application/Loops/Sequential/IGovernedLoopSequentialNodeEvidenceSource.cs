using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Resolves bounded causal coordinates for already-retained node evidence by its canonical digest.</summary>
public interface IGovernedLoopSequentialNodeEvidenceSource
{
    /// <summary>Resolves retained evidence without granting authority or mutating run state.</summary>
    Task<GovernedLoopSequentialNodeEvidenceReceipt?> ResolveAsync(
        string evidenceHash,
        CancellationToken cancellationToken = default);
}

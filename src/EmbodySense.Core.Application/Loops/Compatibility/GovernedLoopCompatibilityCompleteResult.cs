using EmbodySense.Core.Application.Loops.Compatibility.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Loops.Compatibility;

/// <summary>Returns one complete, revision-bound canonical execution evidence set.</summary>
/// <remarks>Current legacy adapters never produce this result; it reserves the explicit successful discriminator for future exact adapters.</remarks>
public sealed class GovernedLoopCompatibilityCompleteResult : GovernedLoopCompatibilityProjectionResult
{
    internal GovernedLoopCompatibilityCompleteResult(GovernedLoopCompatibilitySource source, GovernedLoopExecutionEvidenceSet evidenceSet)
        : base(source, GovernedLoopCompatibilityProjectionStatus.Complete, [])
    {
        ArgumentNullException.ThrowIfNull(evidenceSet);
        EvidenceSet = evidenceSet;
    }

    /// <summary>Gets the complete canonical evidence set.</summary>
    public GovernedLoopExecutionEvidenceSet EvidenceSet { get; }
}

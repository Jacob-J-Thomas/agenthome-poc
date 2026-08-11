using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Authority;

/// <summary>Reports that a governed effect was stopped before its protected continuation could be invoked.</summary>
/// <remarks>The exception carries only bounded authority evidence and never implies that an effect may be retried safely.</remarks>
public sealed class GovernedLoopEffectAuthorityStoppedException : Exception
{
    /// <summary>Creates one exact stopped-boundary projection for an adapter.</summary>
    /// <param name="message">The bounded operator-safe stop detail.</param>
    /// <param name="executionStatus">The Application boundary outcome.</param>
    /// <param name="evidenceStatus">The append-only evidence-store outcome.</param>
    /// <param name="decision">The validated authority decision when one could be constructed.</param>
    public GovernedLoopEffectAuthorityStoppedException(
        string message,
        GovernedLoopEffectAuthorityExecutionStatus executionStatus,
        GovernedLoopEffectAuthorityEvidenceStoreStatus evidenceStatus,
        GovernedLoopEffectAuthorityDecision? decision)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ExecutionStatus = executionStatus;
        EvidenceStatus = evidenceStatus;
        Decision = decision;
    }

    /// <summary>Gets the Application boundary outcome.</summary>
    public GovernedLoopEffectAuthorityExecutionStatus ExecutionStatus { get; }

    /// <summary>Gets the append-only evidence-store outcome.</summary>
    public GovernedLoopEffectAuthorityEvidenceStoreStatus EvidenceStatus { get; }

    /// <summary>Gets the immutable authority decision when one could be constructed.</summary>
    public GovernedLoopEffectAuthorityDecision? Decision { get; }
}

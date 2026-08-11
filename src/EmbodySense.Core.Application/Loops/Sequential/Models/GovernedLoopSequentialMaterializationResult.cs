using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Returns the exact durable run and guarded hand-off when materialization is safe.</summary>
/// <param name="Status">The closed materialization status.</param>
/// <param name="Run">The authenticated durable run when one is available.</param>
/// <param name="Anchor">The exact guard-issued canonical anchor when the request composed.</param>
/// <param name="Detail">A bounded non-secret diagnostic.</param>
public sealed record GovernedLoopSequentialMaterializationResult(
    GovernedLoopSequentialMaterializationStatus Status,
    CustomLoopRunRecord? Run,
    GovernedLoopSequentialRunAnchor? Anchor,
    string Detail)
{
    /// <summary>Gets whether the exact run has a durable admission-audit boundary and may be considered for lifecycle-aware execution.</summary>
    public bool IsReady => (Status is GovernedLoopSequentialMaterializationStatus.Ready or GovernedLoopSequentialMaterializationStatus.Replayed)
        && Run is not null
        && CustomLoopRunValidator.HasCompleteAdmissionAudit(Run);
}

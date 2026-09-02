using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests resolution of one exact immutable registered reconciliation-probe pin.</summary>
/// <param name="Contract">The exact actuator and reconciliation-probe contract to resolve.</param>
public sealed record GovernedLoopEffectReconciliationProbeRegistryReadRequest(GovernedLoopEffectReconciliationContractMetadata Contract)
{
    /// <summary>Gets a detached exact actuator and probe contract.</summary>
    public GovernedLoopEffectReconciliationContractMetadata Contract { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredMetadata(Contract, nameof(Contract));
}

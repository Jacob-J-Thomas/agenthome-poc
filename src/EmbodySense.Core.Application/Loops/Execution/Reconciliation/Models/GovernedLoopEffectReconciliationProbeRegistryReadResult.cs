using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns resolution of one exact immutable registered reconciliation-probe pin.</summary>
/// <param name="Status">The closed exact registry-read disposition.</param>
/// <param name="Contract">The detached current registered contract when safely observed.</param>
/// <param name="Probe">The matching read-only probe only for an exact found pin.</param>
public sealed record GovernedLoopEffectReconciliationProbeRegistryReadResult(
    GovernedLoopEffectReconciliationProbeRegistryReadStatus Status,
    GovernedLoopEffectReconciliationContractMetadata? Contract,
    IGovernedLoopEffectReconciliationProbe? Probe)
{
    /// <summary>Gets the validated closed exact registry-read disposition.</summary>
    public GovernedLoopEffectReconciliationProbeRegistryReadStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));

    /// <summary>Gets a detached immutable current registered contract.</summary>
    public GovernedLoopEffectReconciliationContractMetadata? Contract { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRegistryReadContract(Status, Contract, Probe, nameof(Contract));

    /// <summary>Gets the matching read-only probe only when found.</summary>
    public IGovernedLoopEffectReconciliationProbe? Probe { get; } = GovernedLoopEffectReconciliationModelGuard.RequireRegistryReadProbe(Status, Contract, Probe, nameof(Probe));
}

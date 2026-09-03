namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Projects one registered reconciliation contract without actuator or implementation identity.</summary>
/// <param name="ContractId">The reconciliation contract identity.</param>
/// <param name="ContractVersion">The positive contract version.</param>
/// <param name="ContractHash">The exact contract content hash.</param>
/// <param name="ProbeContractId">The registered read-only probe contract identity.</param>
/// <param name="ProbeContractVersion">The positive probe contract version.</param>
/// <param name="ProbeContractHash">The exact probe contract hash.</param>
public sealed record GovernedLoopEffectReconciliationContractProjection(string ContractId, int ContractVersion, string ContractHash, string ProbeContractId, int ProbeContractVersion, string ProbeContractHash)
{

    /// <summary>Gets the reconciliation contract identity.</summary>
    public string ContractId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(ContractId, nameof(ContractId));
    /// <summary>Gets the positive contract version.</summary>
    public int ContractVersion { get; } = ContractVersion > 0 ? ContractVersion : throw new ArgumentOutOfRangeException(nameof(ContractVersion));
    /// <summary>Gets the exact contract content hash.</summary>
    public string ContractHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(ContractHash, nameof(ContractHash));
    /// <summary>Gets the registered read-only probe contract identity.</summary>
    public string ProbeContractId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(ProbeContractId, nameof(ProbeContractId));
    /// <summary>Gets the positive probe contract version.</summary>
    public int ProbeContractVersion { get; } = ProbeContractVersion > 0 ? ProbeContractVersion : throw new ArgumentOutOfRangeException(nameof(ProbeContractVersion));
    /// <summary>Gets the exact probe contract hash.</summary>
    public string ProbeContractHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(ProbeContractHash, nameof(ProbeContractHash));
}

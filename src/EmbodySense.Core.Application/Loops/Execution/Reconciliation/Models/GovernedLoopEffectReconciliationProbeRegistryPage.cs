using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one bounded deterministic registered reconciliation-probe page.</summary>
/// <param name="Status">The closed registry-list disposition.</param>
/// <param name="Contracts">The ordered immutable registered actuator and probe contracts.</param>
/// <param name="NextCursor">The opaque continuation, or <see langword="null"/> when no page remains.</param>
public sealed record GovernedLoopEffectReconciliationProbeRegistryPage(
    GovernedLoopEffectReconciliationProbeRegistryListStatus Status,
    IReadOnlyList<GovernedLoopEffectReconciliationContractMetadata> Contracts,
    string? NextCursor)
{
    /// <summary>Gets the validated closed registry-list disposition.</summary>
    public GovernedLoopEffectReconciliationProbeRegistryListStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));

    /// <summary>Gets detached immutable registered actuator and probe contracts.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationContractMetadata> Contracts { get; } = GovernedLoopEffectReconciliationModelGuard.CaptureResultPage(Status, GovernedLoopEffectReconciliationProbeRegistryListStatus.Ready, Contracts, NextCursor, item => GovernedLoopEffectReconciliationModelGuard.CopyRequiredMetadata(item, nameof(Contracts)), nameof(Contracts));

    /// <summary>Gets the bounded opaque continuation.</summary>
    public string? NextCursor { get; } = GovernedLoopEffectReconciliationModelGuard.CaptureResultCursor(Status, GovernedLoopEffectReconciliationProbeRegistryListStatus.Ready, NextCursor, nameof(NextCursor));
}

using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Supplies one registered read-only probe with only independent value-free identity and target evidence.</summary>
/// <param name="ProbeInvocationId">The independent probe invocation identity, never the original actuator operation identity.</param>
/// <param name="Case">The value-free exact case reference.</param>
/// <param name="SourceId">The registered probe-source identity.</param>
/// <param name="SourceRegistrationHash">The exact immutable source registration hash.</param>
/// <param name="SourceReliabilityPosture">The registered source reliability posture.</param>
/// <param name="Target">The exact registered target that may be inspected.</param>
public sealed record GovernedLoopEffectReconciliationProbeInvocationRequest(
    string ProbeInvocationId,
    GovernedLoopEffectReconciliationCaseReference Case,
    string SourceId,
    string SourceRegistrationHash,
    GovernedLoopEffectReconciliationReliabilityPosture SourceReliabilityPosture,
    GovernedLoopEffectReconciliationProbeTarget Target)
{
    /// <summary>Gets the independent probe operation identity.</summary>
    public string ProbeInvocationId { get; } = GovernedLoopEffectReconciliationModelGuard.RequireIdentifier(ProbeInvocationId, nameof(ProbeInvocationId));

    /// <summary>Gets the detached value-free exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReference(Case, nameof(Case));

    /// <summary>Gets the registered probe-source identity.</summary>
    public string SourceId { get; } = GovernedLoopEffectReconciliationModelGuard.RequireIdentifier(SourceId, nameof(SourceId));

    /// <summary>Gets the exact immutable source registration hash.</summary>
    public string SourceRegistrationHash { get; } = GovernedLoopEffectReconciliationModelGuard.RequireSha256(SourceRegistrationHash, nameof(SourceRegistrationHash));

    /// <summary>Gets the registered source reliability posture.</summary>
    public GovernedLoopEffectReconciliationReliabilityPosture SourceReliabilityPosture { get; } = Enum.IsDefined(SourceReliabilityPosture) && SourceReliabilityPosture != GovernedLoopEffectReconciliationReliabilityPosture.Unknown
        ? SourceReliabilityPosture
        : throw new ArgumentOutOfRangeException(nameof(SourceReliabilityPosture));

    /// <summary>Gets a detached exact registered probe target.</summary>
    public GovernedLoopEffectReconciliationProbeTarget Target { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredProbeTarget(Target, nameof(Target));
}

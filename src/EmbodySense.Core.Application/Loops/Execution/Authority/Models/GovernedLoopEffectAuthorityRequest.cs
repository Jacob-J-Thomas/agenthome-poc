using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Authority.Models;

/// <summary>Requests one immediate effect-authority decision for an exact admitted graph node and attempt.</summary>
/// <param name="AdmissionReceipt">The complete immutable admission proof retained by the run.</param>
/// <param name="ExecutionBinding">The exact run, graph revision, and frontier generation.</param>
/// <param name="GraphArtifact">The exact immutable graph artifact whose hash was admitted.</param>
/// <param name="NodeId">The exact originating graph-node identity.</param>
/// <param name="NodeAttempt">The exact positive node-attempt number.</param>
/// <param name="EffectOperationId">The stable idempotency identity of the effect.</param>
/// <param name="CorrelationId">The exact boundary-local request or publication identity.</param>
/// <param name="BoundaryKind">The exact irreversible-commit boundary being evaluated.</param>
/// <param name="RequiredAuthority">The complete non-granting authority required by only this effect.</param>
/// <param name="RequiredCapabilityPins">The exact admitted capability pins required by only this effect.</param>
/// <param name="TargetFingerprint">The optional SHA-256 identity of the stable server-owned effect target; workspace intake and actuation share a resolved-path identity, while publication uses immutable invocation evidence.</param>
public sealed record GovernedLoopEffectAuthorityRequest(
    GovernedLoopAdmissionReceipt AdmissionReceipt,
    GovernedLoopExecutionBinding ExecutionBinding,
    GovernedLoopGraphRevisionArtifact GraphArtifact,
    string NodeId,
    int NodeAttempt,
    string EffectOperationId,
    string CorrelationId,
    GovernedLoopEffectBoundaryKind BoundaryKind,
    AuthorityCeiling RequiredAuthority,
    IReadOnlyList<CapabilityAdmissionPin> RequiredCapabilityPins,
    string? TargetFingerprint = null)
{
    /// <summary>Gets a defensive copy of the effect-local non-granting authority requirement.</summary>
    public AuthorityCeiling RequiredAuthority { get; } = RequiredAuthority is null
        ? null!
        : new AuthorityCeiling(
            RequiredAuthority.Capabilities?.ToArray()!,
            RequiredAuthority.DataClasses?.ToArray()!,
            RequiredAuthority.MaxTargetCount,
            RequiredAuthority.MaxSideEffectClass,
            RequiredAuthority.AllowsRecurrence,
            RequiredAuthority.AllowsExternalPublication,
            RequiredAuthority.AllowsIrreversibleAction);

    /// <summary>Gets a defensive snapshot of the exact effect-local capability pins.</summary>
    public IReadOnlyList<CapabilityAdmissionPin> RequiredCapabilityPins { get; } = RequiredCapabilityPins is null ? null! : Array.AsReadOnly(RequiredCapabilityPins.ToArray());
}

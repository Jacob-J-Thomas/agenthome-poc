using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Authority.Models;

/// <summary>Records one immutable decision at one exact governed-loop effect boundary.</summary>
/// <param name="SchemaVersion">The decision schema version, which must be 1.</param>
/// <param name="RunId">The exact admitted run identity.</param>
/// <param name="ExecutionGeneration">The exact positive frontier generation.</param>
/// <param name="NodeId">The exact originating graph-node identity.</param>
/// <param name="NodeAttempt">The exact positive node-attempt number.</param>
/// <param name="EffectOperationId">The stable idempotency identity of the effect.</param>
/// <param name="CorrelationId">The exact boundary-local request or publication correlation identity.</param>
/// <param name="BoundaryKind">The exact irreversible-commit boundary being evaluated.</param>
/// <param name="AdmissionReceiptHash">The canonical hash of the complete successful admission receipt.</param>
/// <param name="AdmittedAuthority">The exact immutable authority proof retained at admission.</param>
/// <param name="CurrentAuthority">The exact current proof, or null only for reasons whose current state could not be resolved.</param>
/// <param name="RequiredAuthority">The complete non-granting node/effect ceiling requested at this boundary.</param>
/// <param name="EffectiveAuthority">The non-granting authority ceiling remaining for this decision.</param>
/// <param name="RequiredCapabilityPins">The exact non-empty capability-pin set required by this effect.</param>
/// <param name="Disposition">The closed direct, pause, or deny disposition.</param>
/// <param name="Reason">The closed reason whose proof composition must match the disposition.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC instant at which current authority was evaluated.</param>
/// <param name="ContentHash">The canonical hash over the complete immutable decision except this field.</param>
public sealed record GovernedLoopEffectAuthorityDecision(
    int SchemaVersion,
    string RunId,
    long ExecutionGeneration,
    string NodeId,
    int NodeAttempt,
    string EffectOperationId,
    string CorrelationId,
    GovernedLoopEffectBoundaryKind BoundaryKind,
    string AdmissionReceiptHash,
    GovernedLoopEffectAuthorityProof AdmittedAuthority,
    GovernedLoopEffectAuthorityProof? CurrentAuthority,
    AuthorityCeiling RequiredAuthority,
    AuthorityCeiling EffectiveAuthority,
    IReadOnlyList<CapabilityAdmissionPin> RequiredCapabilityPins,
    GovernedLoopEffectAuthorityDisposition Disposition,
    GovernedLoopEffectAuthorityReason Reason,
    DateTimeOffset EvaluatedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental decision schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopEffectAuthorityContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensively copied admitted proof.</summary>
    public GovernedLoopEffectAuthorityProof AdmittedAuthority { get; } = GovernedLoopEffectAuthorityContractCopy.Copy(AdmittedAuthority);

    /// <summary>Gets a defensively copied current proof when resolution was conclusive.</summary>
    public GovernedLoopEffectAuthorityProof? CurrentAuthority { get; } = CurrentAuthority is null ? null : GovernedLoopEffectAuthorityContractCopy.Copy(CurrentAuthority);

    /// <summary>Gets a defensively copied non-granting effective ceiling.</summary>
    public AuthorityCeiling EffectiveAuthority { get; } = GovernedLoopEffectAuthorityContractCopy.Copy(EffectiveAuthority);

    /// <summary>Gets a defensively copied non-granting node/effect ceiling.</summary>
    public AuthorityCeiling RequiredAuthority { get; } = GovernedLoopEffectAuthorityContractCopy.Copy(RequiredAuthority);

    /// <summary>Gets the defensively copied exact capability-pin set required by this effect.</summary>
    public IReadOnlyList<CapabilityAdmissionPin> RequiredCapabilityPins { get; } = GovernedLoopEffectAuthorityContractCopy.Copy(RequiredCapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxRequiredCapabilityPins);
}

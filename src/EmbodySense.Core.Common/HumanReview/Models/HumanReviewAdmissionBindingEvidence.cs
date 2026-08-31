namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Retains the exact schema-1 Human Review admission binding in the append-only admission event.</summary>
/// <param name="SchemaVersion">The evidence schema version, which must be 1.</param>
/// <param name="BindingHash">The canonical hash of the complete immutable request binding.</param>
/// <param name="FrontierId">The exact parked frontier identity.</param>
/// <param name="FrontierVersion">The exact parked frontier version.</param>
/// <param name="FrontierHash">The exact canonical parked-frontier hash.</param>
/// <param name="EffectAttempt">The complete optional pre-dispatch effect identity.</param>
/// <param name="ExecutionGeneration">The exact server-owned execution generation captured with the parked frontier.</param>
/// <param name="EvidenceHash">The canonical hash over every preceding evidence field.</param>
/// <remarks>Archived review validation requires this evidence and compares it with the immutable request and current run adapter. Missing or substituted evidence fails closed.</remarks>
public sealed record HumanReviewAdmissionBindingEvidence(
    int SchemaVersion,
    string BindingHash,
    string FrontierId,
    long FrontierVersion,
    string FrontierHash,
    HumanReviewEffectAttemptBinding? EffectAttempt,
    long ExecutionGeneration,
    string EvidenceHash)
{
    /// <summary>Gets the only supported admission-binding evidence schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}

using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Captures one deterministic current model-routing denial without provider secrets or ambient configuration.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="NodeId">The exact reachable Inference node.</param>
/// <param name="NodeTypeId">The exact admitted node implementation type.</param>
/// <param name="PolicyHash">The exact typed routing policy hash.</param>
/// <param name="CandidateProfileId">The exact candidate when resolution reached one, otherwise null.</param>
/// <param name="Reason">The stable definitive denial classification.</param>
/// <param name="EffectiveAuthorityReferenceHash">The exact effective-authority evidence reference.</param>
/// <param name="CapabilityAdmissionReferenceHash">The exact capability-admission evidence reference.</param>
/// <param name="EvaluatedAtUtc">The trusted routing-evaluation time.</param>
public sealed record GovernedLoopAdmissionModelRoutingDenialProof(
    int SchemaVersion,
    string NodeId,
    string NodeTypeId,
    string PolicyHash,
    CapabilityId? CandidateProfileId,
    GovernedLoopAdmissionModelRoutingDenialReason Reason,
    string EffectiveAuthorityReferenceHash,
    string CapabilityAdmissionReferenceHash,
    DateTimeOffset EvaluatedAtUtc)
{
    /// <summary>Gets the only supported experimental denial-proof schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopAdmissionLimits.CurrentSchemaVersion;
}

using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Captures exact immutable, non-secret evidence supporting one successful governed-loop admission.</summary>
/// <param name="SchemaVersion">The evidence schema version, which must be 1.</param>
/// <param name="IntentHash">The canonical hash of the exact stable admission intent.</param>
/// <param name="Binding">The server-owned exact execution binding at generation 1.</param>
/// <param name="GrantProfile">The exact immutable authority-profile revision pinned by the admitted grant.</param>
/// <param name="GrantBoundary">The exact effective, expiry, and completion boundary of the admitted grant revision.</param>
/// <param name="GrantDependencyEvidenceHash">The canonical lowercase SHA-256 digest proving the grant's exact active dependencies.</param>
/// <param name="EffectiveAuthority">The effective ceiling after every source has narrowed authority.</param>
/// <param name="CapabilityAdmission">The exact immutable capability-resolution snapshot.</param>
/// <param name="ModelRoutingAdmission">The exact deterministic model-routing snapshot, explicitly empty when no Inference node is reachable.</param>
/// <param name="References">The bounded exact evidence references, excluding payloads and diagnostics.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC evaluation time.</param>
/// <param name="ContentHash">The canonical hash over this complete evidence record except this field.</param>
public sealed record GovernedLoopAdmissionEvidence(
    int SchemaVersion,
    string IntentHash,
    GovernedLoopExecutionBinding Binding,
    AuthorityGrantProfilePin GrantProfile,
    AuthorityGrantBoundary GrantBoundary,
    string GrantDependencyEvidenceHash,
    AuthorityCeiling EffectiveAuthority,
    CapabilityAdmissionSnapshot CapabilityAdmission,
    GovernedModelRoutingAdmissionSnapshot ModelRoutingAdmission,
    IReadOnlyList<GovernedLoopAdmissionEvidenceReference> References,
    DateTimeOffset EvaluatedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental evidence schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopAdmissionLimits.CurrentSchemaVersion;

    /// <summary>Gets the defensively copied exact authority-profile revision pin.</summary>
    public AuthorityGrantProfilePin GrantProfile { get; } = GovernedLoopAdmissionContractCopy.Copy(GrantProfile);

    /// <summary>Gets the defensively copied exact grant lifecycle boundary.</summary>
    public AuthorityGrantBoundary GrantBoundary { get; } = GovernedLoopAdmissionContractCopy.Copy(GrantBoundary);

    /// <summary>Gets the defensively copied effective authority ceiling.</summary>
    public AuthorityCeiling EffectiveAuthority { get; } = GovernedLoopAdmissionContractCopy.Copy(EffectiveAuthority);

    /// <summary>Gets the defensively copied immutable capability-resolution snapshot.</summary>
    public CapabilityAdmissionSnapshot CapabilityAdmission { get; } = GovernedLoopAdmissionContractCopy.Copy(CapabilityAdmission);

    /// <summary>Gets the immutable routing snapshot atomically retained by this admission receipt.</summary>
    public GovernedModelRoutingAdmissionSnapshot ModelRoutingAdmission { get; } = GovernedLoopAdmissionContractCopy.Copy(ModelRoutingAdmission);

    /// <summary>Gets the defensively copied bounded evidence references.</summary>
    public IReadOnlyList<GovernedLoopAdmissionEvidenceReference> References { get; } = GovernedLoopAdmissionContractCopy.Copy(References);
}

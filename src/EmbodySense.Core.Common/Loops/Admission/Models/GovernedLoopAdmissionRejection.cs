namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Captures one immutable definitive admission rejection without retaining values or diagnostics.</summary>
/// <param name="SchemaVersion">The rejection schema version, which must be 1.</param>
/// <param name="Intent">The exact prepared intent rejected by current exact evidence.</param>
/// <param name="FailureCode">The definitive value-free denial classification.</param>
/// <param name="AuthorityDenial">The exact structured authority denial proof, only for authority denial.</param>
/// <param name="CapabilityDenial">The exact structured capability-policy denial proof, only for capability denial.</param>
/// <param name="References">The exact bounded evidence supporting the rejection.</param>
/// <param name="RejectedAtUtc">The trusted UTC rejection time.</param>
/// <param name="ContentHash">The canonical hash over the complete rejection except this field.</param>
/// <param name="ModelRoutingDenial">The exact structured model-routing denial proof, only for model-routing denial.</param>
public sealed record GovernedLoopAdmissionRejection(
    int SchemaVersion,
    GovernedLoopAdmissionIntent Intent,
    GovernedLoopAdmissionFailureCode FailureCode,
    GovernedLoopAdmissionAuthorityDenialProof? AuthorityDenial,
    GovernedLoopAdmissionCapabilityDenialProof? CapabilityDenial,
    IReadOnlyList<GovernedLoopAdmissionEvidenceReference> References,
    DateTimeOffset RejectedAtUtc,
    string ContentHash,
    GovernedLoopAdmissionModelRoutingDenialProof? ModelRoutingDenial = null)
{
    /// <summary>Gets the only supported experimental rejection schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopAdmissionLimits.CurrentSchemaVersion;

    /// <summary>Gets the defensively copied authority denial proof when this is an authority-policy rejection.</summary>
    public GovernedLoopAdmissionAuthorityDenialProof? AuthorityDenial { get; } = AuthorityDenial is null ? null : GovernedLoopAdmissionContractCopy.Copy(AuthorityDenial);

    /// <summary>Gets the defensively copied capability denial proof when this is a capability-policy rejection.</summary>
    public GovernedLoopAdmissionCapabilityDenialProof? CapabilityDenial { get; } = CapabilityDenial is null ? null : GovernedLoopAdmissionContractCopy.Copy(CapabilityDenial);

    /// <summary>Gets the defensively copied model-routing denial proof when this is a routing-policy rejection.</summary>
    public GovernedLoopAdmissionModelRoutingDenialProof? ModelRoutingDenial { get; } = ModelRoutingDenial is null ? null : ModelRoutingDenial with { };

    /// <summary>Gets the defensively copied bounded evidence references.</summary>
    public IReadOnlyList<GovernedLoopAdmissionEvidenceReference> References { get; } = GovernedLoopAdmissionContractCopy.Copy(References);
}

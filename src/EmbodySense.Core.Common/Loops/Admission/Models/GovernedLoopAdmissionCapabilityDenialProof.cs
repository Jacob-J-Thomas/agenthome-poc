using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Captures a reproducible capability-policy denial without catalog, provider, or diagnostic data.</summary>
/// <param name="SchemaVersion">The proof schema version, which must be 1.</param>
/// <param name="Requirements">The exact bounded dependency manifest evaluated for admission.</param>
/// <param name="RequirementsHash">The canonical hash of <paramref name="Requirements"/>.</param>
/// <param name="EffectiveAuthority">The exact non-widening authority ceiling used for the policy comparison.</param>
/// <param name="Violations">The canonical nonempty set of required root dependencies outside the effective ceiling.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC evaluation time.</param>
public sealed record GovernedLoopAdmissionCapabilityDenialProof(
    int SchemaVersion,
    CapabilityDependencyManifest Requirements,
    string RequirementsHash,
    AuthorityCeiling EffectiveAuthority,
    IReadOnlyList<GovernedLoopAdmissionCapabilityDenialViolation> Violations,
    DateTimeOffset EvaluatedAtUtc)
{
    /// <summary>Gets the only supported experimental proof schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopAdmissionLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensively copied dependency manifest.</summary>
    public CapabilityDependencyManifest Requirements { get; } = GovernedLoopAdmissionContractCopy.Copy(Requirements);

    /// <summary>Gets a defensively copied effective authority ceiling.</summary>
    public AuthorityCeiling EffectiveAuthority { get; } = GovernedLoopAdmissionContractCopy.Copy(EffectiveAuthority);

    /// <summary>Gets a defensively copied bounded violation set.</summary>
    public IReadOnlyList<GovernedLoopAdmissionCapabilityDenialViolation> Violations { get; } = GovernedLoopAdmissionContractCopy.Copy(Violations);
}

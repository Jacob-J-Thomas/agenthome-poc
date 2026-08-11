using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Authority.Models;

/// <summary>Captures one exact bounded authority and capability proof at a trusted evaluation boundary.</summary>
/// <param name="SchemaVersion">The proof schema version, which must be 1.</param>
/// <param name="Grant">The exact immutable authority-grant revision and content hash.</param>
/// <param name="Binding">The exact profile, role, and published-loop binding.</param>
/// <param name="GrantStatus">The closed grant lifecycle posture observed for this proof.</param>
/// <param name="GrantPosture">The closed grant-and-bound-dependency resolution posture.</param>
/// <param name="Boundary">The exact trusted-time and completion boundary.</param>
/// <param name="Ceiling">The exact authority ceiling remaining after all proof-local narrowing.</param>
/// <param name="CapabilityPins">The bounded exact currently valid capability pins inside the proof ceiling.</param>
/// <param name="ObservedCapabilityPins">The bounded exact non-active capability observations retained only to prove drift.</param>
/// <param name="DependencyEvidenceHash">The optional canonical hash of complete dependency evidence; active direct proof requires it.</param>
/// <remarks>This immutable evidence grants nothing by itself and intentionally duplicates the exact historical inputs used by a decision.</remarks>
public sealed record GovernedLoopEffectAuthorityProof(
    int SchemaVersion,
    AuthorityGrantReference Grant,
    AuthorityGrantBinding Binding,
    AuthorityGrantLifecycleStatus GrantStatus,
    GovernedLoopEffectAuthorityGrantPosture GrantPosture,
    AuthorityGrantBoundary Boundary,
    AuthorityCeiling Ceiling,
    IReadOnlyList<CapabilityAdmissionPin> CapabilityPins,
    IReadOnlyList<CapabilityAdmissionPin> ObservedCapabilityPins,
    string? DependencyEvidenceHash)
{
    /// <summary>Gets the only supported experimental proof schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopEffectAuthorityContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensively copied exact binding.</summary>
    public AuthorityGrantBinding Binding { get; } = GovernedLoopEffectAuthorityContractCopy.Copy(Binding);

    /// <summary>Gets a defensively copied exact boundary.</summary>
    public AuthorityGrantBoundary Boundary { get; } = GovernedLoopEffectAuthorityContractCopy.Copy(Boundary);

    /// <summary>Gets a defensively copied authority ceiling.</summary>
    public AuthorityCeiling Ceiling { get; } = GovernedLoopEffectAuthorityContractCopy.Copy(Ceiling);

    /// <summary>Gets a defensively copied bounded capability-pin snapshot.</summary>
    public IReadOnlyList<CapabilityAdmissionPin> CapabilityPins { get; } = GovernedLoopEffectAuthorityContractCopy.Copy(CapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins);

    /// <summary>Gets a defensively copied bounded non-active capability-observation snapshot.</summary>
    public IReadOnlyList<CapabilityAdmissionPin> ObservedCapabilityPins { get; } = GovernedLoopEffectAuthorityContractCopy.Copy(ObservedCapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins);
}

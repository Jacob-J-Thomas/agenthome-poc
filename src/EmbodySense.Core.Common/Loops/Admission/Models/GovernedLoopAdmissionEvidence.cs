using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Captures exact immutable, non-secret evidence supporting one successful governed-loop admission.</summary>
/// <param name="SchemaVersion">The evidence schema version, which must be 1.</param>
/// <param name="IntentHash">The canonical hash of the exact stable admission intent.</param>
/// <param name="Binding">The server-owned exact execution binding at generation 1.</param>
/// <param name="EffectiveAuthority">The effective ceiling after every source has narrowed authority.</param>
/// <param name="CapabilityAdmission">The exact immutable capability-resolution snapshot.</param>
/// <param name="References">The bounded exact evidence references, excluding payloads and diagnostics.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC evaluation time.</param>
/// <param name="ContentHash">The canonical hash over this complete evidence record except this field.</param>
public sealed record GovernedLoopAdmissionEvidence(
    int SchemaVersion,
    string IntentHash,
    GovernedLoopExecutionBinding Binding,
    AuthorityCeiling EffectiveAuthority,
    CapabilityAdmissionSnapshot CapabilityAdmission,
    IReadOnlyList<GovernedLoopAdmissionEvidenceReference> References,
    DateTimeOffset EvaluatedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental evidence schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopAdmissionLimits.CurrentSchemaVersion;

    /// <summary>Gets the defensively copied effective authority ceiling.</summary>
    public AuthorityCeiling EffectiveAuthority { get; } = GovernedLoopAdmissionContractCopy.Copy(EffectiveAuthority);

    /// <summary>Gets the defensively copied immutable capability-resolution snapshot.</summary>
    public CapabilityAdmissionSnapshot CapabilityAdmission { get; } = GovernedLoopAdmissionContractCopy.Copy(CapabilityAdmission);

    /// <summary>Gets the defensively copied bounded evidence references.</summary>
    public IReadOnlyList<GovernedLoopAdmissionEvidenceReference> References { get; } = GovernedLoopAdmissionContractCopy.Copy(References);
}

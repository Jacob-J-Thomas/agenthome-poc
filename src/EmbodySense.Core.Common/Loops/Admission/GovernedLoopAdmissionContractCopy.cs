using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Common.Loops.Admission;

internal static class GovernedLoopAdmissionContractCopy
{
    internal static AuthorityCeiling Copy(AuthorityCeiling? value)
        => value is null || value.Capabilities is null || value.DataClasses is null
            ? null!
            : new AuthorityCeiling(
                Snapshot(value.Capabilities, AuthorityContractLimits.MaxCapabilitiesPerCeiling),
                Snapshot(value.DataClasses, AuthorityContractLimits.MaxDataClassesPerCeiling),
                value.MaxTargetCount,
                value.MaxSideEffectClass,
                value.AllowsRecurrence,
                value.AllowsExternalPublication,
                value.AllowsIrreversibleAction);

    internal static AuthorityBoundaryReceipt Copy(AuthorityBoundaryReceipt? value)
        => value is null || value.Conditions is null || value.Profiles is null
            ? null!
            : new AuthorityBoundaryReceipt(
                value.SchemaVersion,
                value.Decision,
                Snapshot(value.Conditions, AuthorityContractLimits.MaxBoundaryConditionsPerReceipt),
                Snapshot(value.Profiles, AuthorityContractLimits.MaxProfilesPerIntersection),
                value.EvaluatedAtUtc);

    internal static GovernedLoopAdmissionAuthorityDenialProof Copy(GovernedLoopAdmissionAuthorityDenialProof value)
        => new(value.SchemaVersion, value.CandidateCeiling, value.EffectiveCeiling, value.BoundaryReceipt);

    internal static GovernedLoopAdmissionCapabilityDenialProof Copy(GovernedLoopAdmissionCapabilityDenialProof value)
        => new(value.SchemaVersion, value.Requirements, value.RequirementsHash, value.EffectiveAuthority, value.Violations, value.EvaluatedAtUtc);

    internal static CapabilityDependencyManifest Copy(CapabilityDependencyManifest? value)
        => value is null || value.Required is null || value.Optional is null
            ? null!
            : new CapabilityDependencyManifest(
                value.SchemaVersion,
                value.Kind,
                value.SubjectId,
                Snapshot(value.Required, CapabilityContractLimits.MaxDependencyManifestDependencies),
                Snapshot(value.Optional, CapabilityContractLimits.MaxDependencyManifestDependencies),
                value.Artifact);

    internal static CapabilityAdmissionSnapshot Copy(CapabilityAdmissionSnapshot? value)
    {
        if (value is null)
        {
            return null!;
        }

        var requirements = Copy(value.Requirements);
        var pins = value.Pins is null ? null! : Snapshot(value.Pins, CapabilityContractLimits.MaxCapabilityAdmissionPins);
        var evidence = value.Evidence is null ? null! : Snapshot(value.Evidence, CapabilityContractLimits.MaxCapabilityAdmissionEvidenceEntries);
        return new CapabilityAdmissionSnapshot(
            value.SchemaVersion,
            value.WorkspaceScopeId,
            requirements,
            value.RequirementsHash,
            pins,
            evidence,
            value.AdmittedAtUtc);
    }

    internal static IReadOnlyList<GovernedLoopAdmissionEvidenceReference> Copy(IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? values)
        => values is null ? null! : Snapshot(values, GovernedLoopAdmissionLimits.MaxEvidenceReferences);

    internal static IReadOnlyList<GovernedLoopAdmissionCapabilityDenialViolation> Copy(IReadOnlyList<GovernedLoopAdmissionCapabilityDenialViolation>? values)
        => values is null ? null! : Snapshot(values, GovernedLoopAdmissionLimits.MaxCapabilityDenialViolations);

    private static IReadOnlyList<TValue> Snapshot<TValue>(IEnumerable<TValue> values, int maximum)
        => Array.AsReadOnly(values.Take(maximum + 1).ToArray());
}

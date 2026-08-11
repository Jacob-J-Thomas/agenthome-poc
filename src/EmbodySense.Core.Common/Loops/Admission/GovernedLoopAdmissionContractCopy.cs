using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Common.Loops.Admission;

internal static class GovernedLoopAdmissionContractCopy
{
    internal static AuthorityGrantProfilePin Copy(AuthorityGrantProfilePin? value)
        => value?.Reference?.ProfileId is null || value.Reference.Revision is null || value.ContentHash is null
            ? null!
            : new AuthorityGrantProfilePin(
                new AuthorityProfileReference(value.Reference.ProfileId, value.Reference.Revision),
                value.ContentHash);

    internal static AuthorityGrantBoundary Copy(AuthorityGrantBoundary? value)
        => value is null
            ? null!
            : new AuthorityGrantBoundary(value.EffectiveAtUtc, value.ExpiresAtUtc, value.CompletionConstraint);

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

    internal static GovernedLoopAdmissionEvidence Copy(GovernedLoopAdmissionEvidence? value)
        => value is null
            ? null!
            : new GovernedLoopAdmissionEvidence(
                value.SchemaVersion,
                value.IntentHash,
                value.Binding,
                value.GrantProfile,
                value.GrantBoundary,
                value.GrantDependencyEvidenceHash,
                value.EffectiveAuthority,
                value.CapabilityAdmission,
                value.References,
                value.EvaluatedAtUtc,
                value.ContentHash);

    internal static GovernedLoopAdmissionReceipt Copy(GovernedLoopAdmissionReceipt? value)
        => value is null
            ? null!
            : new GovernedLoopAdmissionReceipt(
                value.SchemaVersion,
                value.Intent is null ? null! : value.Intent with { },
                Copy(value.Evidence),
                value.RecordedAtUtc,
                value.ContentHash);

    private static IReadOnlyList<TValue> Snapshot<TValue>(IEnumerable<TValue> values, int maximum)
        => Array.AsReadOnly(values.Take(maximum + 1).ToArray());
}

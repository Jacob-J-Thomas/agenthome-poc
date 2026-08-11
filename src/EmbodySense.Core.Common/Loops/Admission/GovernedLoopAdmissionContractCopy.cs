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

    internal static CapabilityAdmissionSnapshot Copy(CapabilityAdmissionSnapshot? value)
    {
        if (value is null)
        {
            return null!;
        }

        var requirements = value.Requirements is null || value.Requirements.Required is null || value.Requirements.Optional is null
            ? null!
            : new CapabilityDependencyManifest(
                value.Requirements.SchemaVersion,
                value.Requirements.Kind,
                value.Requirements.SubjectId,
                Snapshot(value.Requirements.Required, CapabilityContractLimits.MaxDependencyManifestDependencies),
                Snapshot(value.Requirements.Optional, CapabilityContractLimits.MaxDependencyManifestDependencies),
                value.Requirements.Artifact);
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

    private static IReadOnlyList<TValue> Snapshot<TValue>(IEnumerable<TValue> values, int maximum)
        => Array.AsReadOnly(values.Take(maximum + 1).ToArray());
}

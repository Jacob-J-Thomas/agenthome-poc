using System.Text;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>Validates the closed immutable shape of persisted capability-admission evidence.</summary>
public static class CapabilityAdmissionSnapshotValidator
{
    private static readonly HashSet<string> _outcomes = new(StringComparer.Ordinal)
    {
        "Selected",
        "OmittedOptional"
    };

    /// <summary>Returns a bounded validation error, or <see langword="null"/> for a structurally valid snapshot.</summary>
    public static string? Validate(CapabilityAdmissionSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.SchemaVersion != CapabilityAdmissionSnapshot.CurrentSchemaVersion || string.IsNullOrWhiteSpace(snapshot.WorkspaceScopeId))
        {
            return "Capability admission evidence is missing or has an unsupported schema.";
        }

        if (snapshot.AdmittedAtUtc == default || snapshot.AdmittedAtUtc.Offset != TimeSpan.Zero || snapshot.Pins is null || snapshot.Evidence is null || snapshot.Requirements is null)
        {
            return "Capability admission evidence is incomplete.";
        }

        if (!CapabilityDependencyManifestHash.TryCompute(snapshot.Requirements, out var hash, out _) || !string.Equals(snapshot.RequirementsHash, hash!.Value, StringComparison.Ordinal))
        {
            return "Capability admission requirement evidence was forged or corrupted.";
        }

        if (snapshot.Pins.Count == 0 || snapshot.Pins.Count > CapabilityContractLimits.MaxCapabilityAdmissionPins
            || snapshot.Pins.Any(pin => pin is null || pin.DescriptorIdentity is null || pin.Implementation is null || pin.Provenance is null || pin.Artifact is null
                || string.IsNullOrWhiteSpace(pin.SafeDescription) || pin.SafeDescription.Length > CapabilityContractLimits.MaxPurposeCharacters)
            || snapshot.Pins.Select(pin => pin.DescriptorIdentity.Id.Value).Distinct(StringComparer.Ordinal).Count() != snapshot.Pins.Count)
        {
            return "Capability admission pins are missing, duplicated, or malformed.";
        }

        if (snapshot.Evidence.Count > CapabilityContractLimits.MaxCapabilityAdmissionEvidenceEntries
            || snapshot.Evidence.Any(item => item is null || item.SubjectId is null || item.DependencyId is null || item.CompatibleVersionRange is null
                || !_outcomes.Contains(item.Outcome) || !IsSafeDetail(item.Detail)
                || string.Equals(item.Outcome, "Selected", StringComparison.Ordinal) != (item.SelectedIdentity is not null)
                || string.Equals(item.Outcome, "OmittedOptional", StringComparison.Ordinal) && !item.IsOptional))
        {
            return "Capability admission resolution evidence is malformed, unsupported, or outside its bounded contract.";
        }

        if (snapshot.Evidence.GroupBy(item => (item.SubjectId.Value, item.DependencyId.Value, item.IsOptional)).Any(group => group.Skip(1).Any()))
        {
            return "Capability admission resolution evidence contains duplicate observations.";
        }

        var selectedEvidence = snapshot.Evidence.Where(item => string.Equals(item.Outcome, "Selected", StringComparison.Ordinal)).ToArray();
        if (selectedEvidence.Any(item => item.SelectedIdentity is null || !item.SelectedIdentity.Id.Equals(item.DependencyId)))
        {
            return "Capability admission pins do not match the immutable selected-resolution evidence.";
        }

        var reachableSubjects = new HashSet<string>(StringComparer.Ordinal) { snapshot.Requirements.SubjectId.Value };
        var remaining = selectedEvidence.ToList();
        while (true)
        {
            var reachable = remaining.Where(item => reachableSubjects.Contains(item.SubjectId.Value)).ToArray();
            if (reachable.Length == 0)
            {
                break;
            }

            foreach (var item in reachable)
            {
                remaining.Remove(item);
                reachableSubjects.Add(item.DependencyId.Value);
            }
        }

        var selectedIdentities = selectedEvidence.Select(item => item.SelectedIdentity!).Distinct().ToArray();
        if (remaining.Count > 0 || snapshot.Evidence.Any(item => !reachableSubjects.Contains(item.SubjectId.Value))
            || selectedIdentities.Length != snapshot.Pins.Count || snapshot.Pins.Any(pin => !selectedIdentities.Contains(pin.DescriptorIdentity)))
        {
            return "Capability admission pins do not match a reachable immutable resolution graph.";
        }

        return null;
    }

    private static bool IsSafeDetail(string? detail)
    {
        return !string.IsNullOrWhiteSpace(detail)
            && detail.Length <= CapabilityContractLimits.MaxCapabilityAdmissionEvidenceDetailCharacters
            && detail.IsNormalized(NormalizationForm.FormC)
            && !detail.Any(character => char.IsControl(character) || char.IsSurrogate(character));
    }
}

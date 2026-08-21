using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;

namespace EmbodySense.Core.Common.Loops.Admission;

internal static class GovernedLoopAdmissionCapabilityGuard
{
    internal static bool IsValid(CapabilityAdmissionSnapshot? snapshot)
    {
        if (snapshot is null
            || snapshot.SchemaVersion != CapabilityAdmissionSnapshot.CurrentSchemaVersion
            || !ContextualRoleWorkspaceId.IsValid(snapshot.WorkspaceScopeId)
            || snapshot.AdmittedAtUtc == default
            || snapshot.AdmittedAtUtc.Offset != TimeSpan.Zero
            || snapshot.Requirements is null
            || !CapabilityDependencyManifestValidator.Validate(snapshot.Requirements).IsValid
            || snapshot.Pins is null
            || snapshot.Pins.Count > CapabilityContractLimits.MaxCapabilityAdmissionPins
            || snapshot.Evidence is null
            || snapshot.Evidence.Count > CapabilityContractLimits.MaxCapabilityAdmissionEvidenceEntries)
        {
            return false;
        }

        return snapshot.Pins.All(CapabilityAdmissionPinValidator.IsValid)
            && snapshot.Evidence.All(IsValidEvidence)
            && CapabilityAdmissionSnapshotValidator.Validate(snapshot) is null;
    }

    private static bool IsValidEvidence(CapabilityAdmissionEvidence? evidence)
    {
        return evidence?.SubjectId is not null
            && evidence.DependencyId is not null
            && evidence.CompatibleVersionRange is not null
            && evidence.SelectedIdentity is not { Id: null }
            && evidence.SelectedIdentity is not { Version: null }
            && evidence.SelectedIdentity is not { Hash: null };
    }

}

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
            || snapshot.Pins.Count is < 1 or > CapabilityContractLimits.MaxCapabilityAdmissionPins
            || snapshot.Evidence is null
            || snapshot.Evidence.Count > CapabilityContractLimits.MaxCapabilityAdmissionEvidenceEntries)
        {
            return false;
        }

        return snapshot.Pins.All(IsValidPin)
            && snapshot.Evidence.All(IsValidEvidence)
            && CapabilityAdmissionSnapshotValidator.Validate(snapshot) is null;
    }

    private static bool IsValidPin(CapabilityAdmissionPin? pin)
    {
        return pin?.DescriptorIdentity is { Id: not null, Version: not null, Hash: not null }
            && Enum.IsDefined(pin.Kind)
            && pin.Kind != CapabilityKind.Unknown
            && pin.Implementation?.ProviderId is not null
            && CapabilityIdentifierRules.IsPath(pin.Implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters)
            && IsValidProvenance(pin.Provenance)
            && IsValidArtifact(pin.Artifact)
            && !string.IsNullOrWhiteSpace(pin.SafeDescription)
            && CapabilityTextRules.IsSafeNormalized(pin.SafeDescription, CapabilityContractLimits.MaxPurposeCharacters, allowEmpty: false);
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

    private static bool IsValidProvenance(CapabilityProvenance? provenance)
    {
        if (provenance is null
            || !Enum.IsDefined(provenance.Kind)
            || provenance.Kind == CapabilityProvenanceKind.Unknown
            || !IsSafeSourceUri(provenance.SourceUri)
            || provenance.SourceRevision is not null && !IsSafeSourceRevision(provenance.SourceRevision))
        {
            return false;
        }

        return provenance.Kind != CapabilityProvenanceKind.RemoteArtifact || provenance.Integrity is not null;
    }

    private static bool IsValidArtifact(CapabilityDependencyArtifactMetadata? artifact)
        => artifact is not null
            && (artifact.Signature is null
                || CapabilityTextRules.IsSafeAsciiToken(artifact.Signature, CapabilityContractLimits.MaxArtifactSignatureCharacters));

    private static bool IsSafeSourceUri(string? value)
    {
        if (!CapabilityTextRules.IsSafeAsciiToken(value, CapabilityContractLimits.MaxSourceUriCharacters)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Scheme is not "https" and not "file" and not "pkg" and not "urn")
        {
            return false;
        }

        return string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal);
    }

    private static bool IsSafeSourceRevision(string value)
        => value.Length is >= 1 and <= CapabilityContractLimits.MaxSourceRevisionCharacters
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or '@');
}

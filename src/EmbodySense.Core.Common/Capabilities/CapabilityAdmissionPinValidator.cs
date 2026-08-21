using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>Validates one exact capability admission pin at public persistence and adapter boundaries.</summary>
public static class CapabilityAdmissionPinValidator
{
    /// <summary>Returns <see langword="true"/> only for a complete canonical schema-1 admission pin.</summary>
    /// <param name="pin">The potentially hostile pin shape.</param>
    /// <returns>Whether every exact identity, implementation, provenance, artifact, and safe-text field is valid.</returns>
    public static bool IsValid(CapabilityAdmissionPin? pin)
    {
        return pin is not null
            && IsValidDescriptorIdentity(pin.DescriptorIdentity)
            && Enum.IsDefined(pin.Kind)
            && pin.Kind != CapabilityKind.Unknown
            && IsValidProvider(pin.Implementation?.ProviderId)
            && CapabilityIdentifierRules.IsPath(pin.Implementation!.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters)
            && IsValidProvenance(pin.Provenance)
            && IsValidArtifact(pin.Artifact)
            && CapabilityTextRules.IsSafeNormalized(pin.SafeDescription, CapabilityContractLimits.MaxPurposeCharacters, allowEmpty: false);
    }

    private static bool IsValidDescriptorIdentity(CapabilityDescriptorIdentity? identity)
    {
        return identity?.Id is not null
            && identity.Version is not null
            && identity.Hash is not null
            && CapabilityId.TryParse(identity.Id.Value, out var id, out _)
            && CapabilityVersion.TryParse(identity.Version.Value, out var version, out _)
            && CapabilityDescriptorHash.TryParse(identity.Hash.Value, out var hash, out _)
            && identity.Id.Equals(id)
            && identity.Version.Equals(version)
            && identity.Hash.Equals(hash);
    }

    private static bool IsValidProvider(CapabilityProviderId? provider)
        => provider is not null && CapabilityProviderId.TryParse(provider.Value, out var parsed, out _) && provider.Equals(parsed);

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

        return (provenance.Integrity is null || IsValidDigest(provenance.Integrity))
            && (provenance.Kind != CapabilityProvenanceKind.RemoteArtifact || provenance.Integrity is not null);
    }

    private static bool IsValidArtifact(CapabilityDependencyArtifactMetadata? artifact)
        => artifact is not null
            && (artifact.Checksum is null || IsValidDigest(artifact.Checksum))
            && (artifact.Signature is null || CapabilityTextRules.IsSafeAsciiToken(artifact.Signature, CapabilityContractLimits.MaxArtifactSignatureCharacters));

    private static bool IsValidDigest(CapabilityIntegrityDigest digest)
        => CapabilityIntegrityDigest.TryParse(digest.Value, out var parsed, out _) && digest.Equals(parsed);

    private static bool IsSafeSourceUri(string? value)
    {
        return CapabilityTextRules.IsSafeAsciiToken(value, CapabilityContractLimits.MaxSourceUriCharacters)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.Scheme is "https" or "file" or "pkg" or "urn"
            && string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal);
    }

    private static bool IsSafeSourceRevision(string value)
        => value.Length is >= 1 and <= CapabilityContractLimits.MaxSourceRevisionCharacters
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or '@');
}

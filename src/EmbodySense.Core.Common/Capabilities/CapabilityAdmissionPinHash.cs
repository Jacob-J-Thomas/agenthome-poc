using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>Computes a domain-separated hash over every semantically protected exact capability-admission pin field.</summary>
public static class CapabilityAdmissionPinHash
{
    /// <summary>Computes the canonical complete pin hash after deep schema-1 validation.</summary>
    public static string Compute(CapabilityAdmissionPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (!CapabilityAdmissionPinValidator.IsValid(pin))
        {
            throw new ArgumentException("Capability admission pin must be exact, canonical, and complete.", nameof(pin));
        }

        return GovernedModelContractHash.Compute("embodysense.capability-admission-pin.v1", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("artifactChecksum", pin.Artifact.Checksum?.Value);
            writer.WriteString("artifactSignature", pin.Artifact.Signature);
            writer.WriteString("descriptorHash", pin.DescriptorIdentity.Hash.Value);
            writer.WriteString("descriptorId", pin.DescriptorIdentity.Id.Value);
            writer.WriteString("descriptorVersion", pin.DescriptorIdentity.Version.Value);
            writer.WriteString("implementationId", pin.Implementation.ImplementationId);
            writer.WriteString("implementationProvider", pin.Implementation.ProviderId.Value);
            writer.WriteNumber("kind", (int)pin.Kind);
            writer.WriteString("provenanceIntegrity", pin.Provenance.Integrity?.Value);
            writer.WriteNumber("provenanceKind", (int)pin.Provenance.Kind);
            writer.WriteString("provenanceSourceRevision", pin.Provenance.SourceRevision);
            writer.WriteString("provenanceSourceUri", pin.Provenance.SourceUri);
            writer.WriteString("safeDescription", pin.SafeDescription);
            writer.WriteEndObject();
        });
    }
}

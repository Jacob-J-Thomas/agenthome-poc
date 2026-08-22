using System.Text.Json;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Pins one exact generic ModelProfile capability and its immutable safe domain metadata.</summary>
public sealed class GovernedModelProfilePin
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelProfilePin(CapabilityAdmissionPin capability, GovernedModelProfileMetadata metadata, string profileSourceRevisionHash, string adapterRegistryRevisionHash)
    {
        Capability = capability;
        Metadata = metadata;
        ProfileSourceRevisionHash = profileSourceRevisionHash;
        AdapterRegistryRevisionHash = adapterRegistryRevisionHash;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-profile-pin.v1", WriteCanonical);
    }

    /// <summary>Gets the exact generic capability admission pin.</summary>
    public CapabilityAdmissionPin Capability { get; }
    /// <summary>Gets exact safe model-profile metadata.</summary>
    public GovernedModelProfileMetadata Metadata { get; }
    /// <summary>Gets the exact trusted profile-source revision used to resolve this pin.</summary>
    public string ProfileSourceRevisionHash { get; }
    /// <summary>Gets the exact adapter-registry revision used to prove this pin dispatchable.</summary>
    public string AdapterRegistryRevisionHash { get; }
    /// <summary>Gets the canonical complete pin hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates a validated exact model-profile pin.</summary>
    public static GovernedModelProfilePin Create(CapabilityAdmissionPin capability, GovernedModelProfileMetadata metadata, string profileSourceRevisionHash, string adapterRegistryRevisionHash)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!CapabilityAdmissionPinValidator.IsValid(capability)
            || !GovernedModelContractValidator.IsValid(metadata)
            || capability.Kind != CapabilityKind.ModelProfile
            || !Equals(capability.DescriptorIdentity, metadata.DescriptorIdentity))
        {
            throw new ArgumentException("The generic capability pin must be a complete exact ModelProfile identity matching the domain metadata.", nameof(capability));
        }

        return new GovernedModelProfilePin(
            capability,
            metadata,
            GovernedModelContractRules.RequireHash(profileSourceRevisionHash, nameof(profileSourceRevisionHash)),
            GovernedModelContractRules.RequireHash(adapterRegistryRevisionHash, nameof(adapterRegistryRevisionHash)));
    }

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("adapterRegistryRevisionHash", AdapterRegistryRevisionHash);
        writer.WriteString("capabilityPinHash", CapabilityAdmissionPinHash.Compute(Capability));
        writer.WriteString("metadataHash", Metadata.ContentHash);
        writer.WriteString("profileSourceRevisionHash", ProfileSourceRevisionHash);
        writer.WriteEndObject();
    }
}

namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Binds a credential lease to one exact admitted capability implementation and secret requirement.</summary>
public sealed record CredentialLeaseCapabilityScope(
    string CapabilityId,
    string CapabilityVersion,
    string CapabilityDescriptorHash,
    string CapabilityProviderId,
    string CapabilityImplementationId,
    string SecretRequirement);

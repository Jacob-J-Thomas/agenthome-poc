using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Contains safe registered metadata and exact binding; it confers neither consent nor authority.</summary>
public sealed record CredentialRegistryEntry(CredentialReference Reference, CredentialCapabilityBinding Binding, CredentialContractHash BindingHash, CredentialContractId ConsentReference, CredentialProviderHealthStatus Health, long Revision, CredentialContractId LastOperationId, bool ConsentGranted = false);

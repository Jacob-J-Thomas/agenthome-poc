using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Returns one value-free credential-registry mutation outcome.</summary>
public sealed record CredentialRegistryMutationResult(CredentialRegistryMutationStatus Status, CredentialContractId OperationId, long? RegistryRevision, CredentialRegistryEntry? Entry, CredentialFailure? Failure);

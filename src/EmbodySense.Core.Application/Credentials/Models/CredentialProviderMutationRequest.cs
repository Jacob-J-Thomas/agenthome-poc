using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Identifies one bounded provider mutation without containing credential bytes or a private locator.</summary>
public sealed record CredentialProviderMutationRequest(string WorkspaceId, CredentialReferenceId ReferenceId, CredentialProviderId ProviderId, CredentialContractId OperationId, int ValueByteLength);

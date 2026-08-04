using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Identifies one provider deletion without carrying credential bytes or a private locator.</summary>
public sealed record CredentialProviderDeleteRequest(string WorkspaceId, CredentialReferenceId ReferenceId, CredentialProviderId ProviderId, CredentialContractId OperationId);

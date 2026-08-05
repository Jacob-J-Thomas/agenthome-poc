using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Identifies one trusted provider callback without carrying authority or a private locator.</summary>
public sealed record CredentialProviderUseRequest(string WorkspaceId, CredentialReferenceId ReferenceId, CredentialProviderId ProviderId, CredentialContractId OperationId);

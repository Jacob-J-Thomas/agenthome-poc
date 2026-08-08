using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Persistence.Credentials;

/// <summary>Fails closed until a provider-owned locator verifier is explicitly composed.</summary>
public sealed class RejectingCredentialProviderLocatorVerifier : ICredentialProviderLocatorVerifier
{
    /// <inheritdoc />
    public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

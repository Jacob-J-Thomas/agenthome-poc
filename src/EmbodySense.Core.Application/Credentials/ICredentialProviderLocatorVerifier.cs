using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Verifies that a provider owns one opaque locator without resolving credential material.</summary>
public interface ICredentialProviderLocatorVerifier
{
    /// <summary>Returns whether the provider currently authenticates this exact workspace-bound locator.</summary>
    ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken);
}

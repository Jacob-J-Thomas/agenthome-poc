using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Supplies a provider-owned private locator only to lifecycle orchestration.</summary>
public interface ICredentialProviderLocatorSource
{
    /// <summary>Creates the private opaque locator for one exact provider registration.</summary>
    /// <remarks>This call may mutate provider-owned state. Cancellation, exceptions, and missing results after invocation are outcome-uncertain and must not be retried automatically.</remarks>
    /// <param name="workspaceId">The exact workspace receiving the provider registration.</param>
    /// <param name="referenceId">The stable value-free credential reference identity.</param>
    /// <param name="providerId">The provider expected to own the locator.</param>
    /// <param name="cancellationToken">Requests cancellation, which does not prove that provider-side creation was avoided.</param>
    /// <returns>The private opaque locator when creation is proved; otherwise <see langword="null"/> with an uncertain provider outcome.</returns>
    ValueTask<CredentialProviderLocator?> CreateAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId providerId, CancellationToken cancellationToken);
}

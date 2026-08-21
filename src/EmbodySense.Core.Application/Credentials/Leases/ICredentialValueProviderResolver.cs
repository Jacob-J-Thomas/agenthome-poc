using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Leases;

/// <summary>Resolves only server-configured trusted providers for exact registry identities.</summary>
public interface ICredentialValueProviderResolver
{
    /// <summary>Resolves one configured provider without accepting a private locator or fallback identity.</summary>
    Task<CredentialValueProviderResolution> ResolveAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId providerId, CancellationToken cancellationToken = default);
}

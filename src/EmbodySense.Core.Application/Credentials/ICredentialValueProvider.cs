using EmbodySense.Core.Application.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Provides replaceable credential storage through value-free requests and span-based callbacks.</summary>
public interface ICredentialValueProvider
{
    /// <summary>Creates provider-owned credential material from an exact-size ephemeral source callback.</summary>
    ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken);
    /// <summary>Atomically replaces provider-owned credential material from an ephemeral source callback.</summary>
    ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken);
    /// <summary>Exposes provider-owned material only to one trusted span callback.</summary>
    ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken);
    /// <summary>Deletes provider-owned material with explicit uncertainty in the returned failure.</summary>
    ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken);
    /// <summary>Gets safe posture without resolving or returning credential material.</summary>
    ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken);
}

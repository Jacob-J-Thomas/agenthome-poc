using EmbodySense.Core.Application.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Provides replaceable credential storage through value-free requests and span-based callbacks.</summary>
public interface ICredentialValueProvider
{
    /// <summary>Creates provider-owned credential material from an exact-size ephemeral source callback.</summary>
    /// <remarks>The source callback is invoked synchronously at most once during this call and must report exactly <see cref="CredentialProviderMutationRequest.ValueByteLength"/> bytes written. Cancellation observed before commit, callback exceptions, partial writes, and count mismatches must return a value-free failure without mutating provider state. On every non-commit exit, the provider must cryptographically zero the complete transient destination before releasing or returning it to any pool. The provider must not retain the callback or transient destination.</remarks>
    ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken);
    /// <summary>Atomically replaces provider-owned credential material from an ephemeral source callback.</summary>
    /// <remarks>The source callback is invoked synchronously at most once during this call and must report exactly <see cref="CredentialProviderMutationRequest.ValueByteLength"/> bytes written. Cancellation observed before commit, callback exceptions, partial writes, and count mismatches must return a value-free failure while preserving the prior value. On every non-commit exit, the provider must cryptographically zero the complete transient destination before releasing or returning it to any pool. The provider must not retain the callback or transient destination.</remarks>
    ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken);
    /// <summary>Exposes provider-owned material only to one trusted span callback.</summary>
    /// <remarks>The trusted consumer is invoked synchronously at most once during this call and must not be retained. Cancellation observed before invocation must return a value-free failure without invoking the consumer; consumer exceptions must return a value-free callback failure.</remarks>
    ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken);
    /// <summary>Deletes provider-owned material with explicit uncertainty in the returned failure.</summary>
    /// <remarks>Cancellation observed before commit must return a value-free unavailable failure without deleting provider state.</remarks>
    ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken);
    /// <summary>Gets safe posture without resolving or returning credential material.</summary>
    /// <remarks>Cancellation or provider unavailability must return a value-free unavailable posture rather than credential material or provider-private diagnostics.</remarks>
    ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken);
}

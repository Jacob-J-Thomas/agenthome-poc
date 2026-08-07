using EmbodySense.Core.Application.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Persists one workspace's value-free credential registry and use evidence.</summary>
public interface ICredentialRegistryStore : ICredentialReferenceStore, ICredentialUseEvidenceSink
{
    /// <summary>Reads one safe registry snapshot without resolving a provider locator or granting authority.</summary>
    Task<CredentialRegistryReadResult> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies one idempotent optimistic registry mutation.</summary>
    Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default);
}

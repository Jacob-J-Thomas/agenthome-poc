using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Persists one workspace's value-free credential registry and use evidence.</summary>
public interface ICredentialRegistryStore : ICredentialReferenceStore, ICredentialUseEvidenceSink
{
    /// <summary>Authenticates one bounded lifecycle actor against this registry composition's closed identity source.</summary>
    ValueTask<CredentialActorAuthentication> AuthenticateActorAsync(string actorId, CancellationToken cancellationToken);

    /// <summary>Reads one safe registry snapshot without resolving a provider locator or granting authority.</summary>
    Task<CredentialRegistryReadResult> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies one idempotent optimistic registry mutation.</summary>
    Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default);

    /// <summary>Appends durable delivery evidence for one lifecycle audit outbox item without advancing the lifecycle revision.</summary>
    /// <param name="auditOperationId">The lifecycle operation whose durable audit item was delivered.</param>
    /// <param name="cancellationToken">Stops the acknowledgement before it is persisted.</param>
    /// <returns><see langword="true"/> when the exact item is already or newly acknowledged; otherwise <see langword="false"/>.</returns>
    Task<bool> AcknowledgeAuditAsync(CredentialContractId auditOperationId, CancellationToken cancellationToken = default);
}

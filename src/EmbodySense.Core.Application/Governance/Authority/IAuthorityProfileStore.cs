using EmbodySense.Core.Application.Governance.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority;

/// <summary>Persists one workspace's non-self-granting authority-profile declarations and lifecycle evidence.</summary>
public interface IAuthorityProfileStore
{
    /// <summary>Reads one profile, all immutable revisions, lifecycle evidence, and operation receipts.</summary>
    /// <param name="profileId">The canonical profile identifier.</param>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>The current profile state, recovered prior proof, or an unavailable result.</returns>
    Task<AuthorityProfileReadResult> ReadAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>Applies one idempotent, optimistic profile lifecycle mutation.</summary>
    /// <param name="mutation">The bounded declaration, revision, transition, or tombstone request.</param>
    /// <param name="cancellationToken">The token used to cancel durable work.</param>
    /// <returns>The durable outcome or a fail-closed availability result.</returns>
    Task<AuthorityProfileMutationResult> MutateAsync(AuthorityProfileMutation mutation, CancellationToken cancellationToken = default);
}

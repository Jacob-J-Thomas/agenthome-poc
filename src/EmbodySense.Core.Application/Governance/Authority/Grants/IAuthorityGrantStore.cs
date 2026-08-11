using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Persists authenticated append-only authority-grant revisions and workspace-global operation evidence.</summary>
public interface IAuthorityGrantStore
{
    /// <summary>Reads one exact grant snapshot without following a replacement identity.</summary>
    /// <param name="grantId">The stable exact grant identity.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The current bounded snapshot or a fail-closed read result.</returns>
    Task<AuthorityGrantStoreReadResult> ReadAsync(AuthorityGrantId grantId, CancellationToken cancellationToken = default);

    /// <summary>Reads one target grant and a workspace-global operation binding for mutation planning.</summary>
    /// <param name="grantId">The stable exact grant identity.</param>
    /// <param name="operationId">The workspace-global operation identity.</param>
    /// <param name="requestHash">The canonical exact-intent hash.</param>
    /// <param name="cancellationToken">A token that cancels work before durable intent.</param>
    /// <returns>The exact optimistic observation.</returns>
    Task<AuthorityGrantStoreReadResult> ReadForMutationAsync(AuthorityGrantId grantId, string operationId, string requestHash, CancellationToken cancellationToken = default);

    /// <summary>Atomically appends one immutable successor and its operation evidence.</summary>
    /// <param name="mutation">The bounded optimistic append request.</param>
    /// <param name="cancellationToken">A token used until durable intent begins.</param>
    /// <returns>The exact durable or fail-closed outcome.</returns>
    Task<AuthorityGrantStoreCommitResult> CommitAsync(AuthorityGrantStoreMutation mutation, CancellationToken cancellationToken = default);
}

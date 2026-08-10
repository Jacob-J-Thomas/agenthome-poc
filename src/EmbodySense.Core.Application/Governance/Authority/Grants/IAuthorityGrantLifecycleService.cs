using EmbodySense.Core.Application.Governance.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Executes authenticated immutable authority-grant lifecycle operations.</summary>
public interface IAuthorityGrantLifecycleService
{
    /// <summary>Mutates one grant through exact replay, authority, dependency, time, and optimistic-store checks.</summary>
    /// <param name="request">The bounded exact-intent request.</param>
    /// <param name="cancellationToken">A token that cancels work before durable intent.</param>
    /// <returns>The exact durable or fail-closed outcome.</returns>
    Task<AuthorityGrantMutationResult> MutateAsync(AuthorityGrantMutationRequest? request, CancellationToken cancellationToken = default);
}

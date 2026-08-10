using EmbodySense.Core.Application.Governance.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Authorizes only exact canonical grant mutation requests through a server-owned authority source.</summary>
public interface IAuthorityGrantActorAuthorizer
{
    /// <summary>Evaluates current authority without accepting a client-supplied approval or effective ceiling.</summary>
    /// <param name="request">The exact canonical request and trusted evaluation instant.</param>
    /// <param name="cancellationToken">A token that cancels authorization.</param>
    /// <returns>An exact echoed value-free decision and evidence digest.</returns>
    Task<AuthorityGrantActorAuthorization> AuthorizeAsync(AuthorityGrantActorAuthorizationRequest request, CancellationToken cancellationToken = default);
}

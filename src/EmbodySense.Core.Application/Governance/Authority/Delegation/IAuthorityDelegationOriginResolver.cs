using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation;

/// <summary>Resolves only the exact server-owned issuer lineage and its authored delegation restrictions.</summary>
public interface IAuthorityDelegationOriginResolver
{
    /// <summary>Resolves the exact issuer named by a creation request without selecting another run, generation, node, or attempt.</summary>
    Task<AuthorityDelegationOriginResolution> ResolveForCreationAsync(AuthorityDelegationCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resolves the exact issuer named by an immutable envelope and intended-use request without following replacements.</summary>
    Task<AuthorityDelegationOriginResolution> ResolveForUseAsync(AuthorityDelegationUseRequest request, CancellationToken cancellationToken = default);
}

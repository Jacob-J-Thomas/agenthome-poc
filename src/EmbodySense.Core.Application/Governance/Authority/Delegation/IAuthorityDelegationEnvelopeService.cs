using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation;

/// <summary>Creates and revalidates non-authorizing delegated-authority evidence.</summary>
public interface IAuthorityDelegationEnvelopeService
{
    /// <summary>Creates an envelope only from exact current parent, origin, target, time, and completion truth.</summary>
    /// <param name="request">The exact bounded creation request.</param>
    /// <param name="cancellationToken">A token that cancels before a conclusive result.</param>
    /// <returns>A hash-valid envelope for <c>Created</c> or an exact <c>Replayed</c> operation.</returns>
    Task<AuthorityDelegationServiceResult> CreateAsync(AuthorityDelegationCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Revalidates one envelope for one exact intended use without invoking protected work.</summary>
    /// <param name="request">The complete envelope and exact current intended use.</param>
    /// <param name="cancellationToken">A token that cancels before a conclusive result.</param>
    /// <returns>The same immutable envelope only for <c>Valid</c>.</returns>
    Task<AuthorityDelegationServiceResult> RevalidateAsync(AuthorityDelegationUseRequest request, CancellationToken cancellationToken = default);
}

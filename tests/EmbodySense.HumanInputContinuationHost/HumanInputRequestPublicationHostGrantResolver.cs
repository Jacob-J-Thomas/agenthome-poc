using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputRequestPublicationHostGrantResolver(AuthorityGrant grant, DateTimeOffset evaluatedAtUtc) : IAuthorityGrantResolver
{
    private readonly AuthorityGrant _grant = grant;
    private readonly DateTimeOffset _evaluatedAtUtc = evaluatedAtUtc;

    public Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference is not null
            && Equals(reference.GrantId, _grant.GrantId)
            && Equals(reference.Revision, _grant.Revision)
            && string.Equals(reference.ContentHash, _grant.ContentHash, StringComparison.Ordinal))
        {
            return Task.FromResult(new AuthorityGrantResolution(
                AuthorityGrantResolutionStatus.Active,
                reference,
                _grant,
                _grant.RequestedCeiling,
                new string('d', 64),
                _evaluatedAtUtc));
        }

        return Task.FromResult(new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.Unavailable,
            reference,
            null,
            AuthorityCeilingIntersection.EmptyCeiling(),
            string.Empty,
            default));
    }
}

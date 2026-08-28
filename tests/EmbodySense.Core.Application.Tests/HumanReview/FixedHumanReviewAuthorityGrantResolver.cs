using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class FixedHumanReviewAuthorityGrantResolver(AuthorityGrantResolution result) : IAuthorityGrantResolver
{
    public Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
        => Task.FromResult(result);
}

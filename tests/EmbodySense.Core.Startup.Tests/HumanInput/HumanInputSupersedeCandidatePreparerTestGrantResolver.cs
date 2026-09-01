using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

internal sealed class HumanInputSupersedeCandidatePreparerTestGrantResolver(AuthorityGrantResolution resolution) : IAuthorityGrantResolver
{
    public Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(resolution);
    }
}

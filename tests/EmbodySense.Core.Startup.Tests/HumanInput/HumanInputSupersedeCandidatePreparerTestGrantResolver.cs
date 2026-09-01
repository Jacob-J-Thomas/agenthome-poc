using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

internal sealed class HumanInputSupersedeCandidatePreparerTestGrantResolver(AuthorityGrantResolution resolution) : IAuthorityGrantResolver
{
    internal AuthorityGrantResolution Resolution { get; set; } = resolution;
    internal Exception? ResolveException { get; set; }

    public Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ResolveException is not null)
        {
            throw ResolveException;
        }

        return Task.FromResult(Resolution);
    }
}

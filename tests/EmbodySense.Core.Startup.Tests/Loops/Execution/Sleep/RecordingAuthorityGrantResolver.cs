using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class RecordingAuthorityGrantResolver(AuthorityGrantResolution resolution) : IAuthorityGrantResolver
{
    public Task<AuthorityGrantResolution> ResolveAsync(
        AuthorityGrantReference? reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(resolution);
    }
}

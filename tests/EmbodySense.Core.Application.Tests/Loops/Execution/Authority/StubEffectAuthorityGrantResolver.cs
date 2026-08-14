using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

internal sealed class StubEffectAuthorityGrantResolver : IAuthorityGrantResolver
{
    internal AuthorityGrantResolution Resolution { get; set; } = null!;

    internal int Calls { get; private set; }

    internal AuthorityGrantReference? LastReference { get; private set; }

    public Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastReference = reference;
        return Task.FromResult(Resolution);
    }
}

using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

internal sealed class HumanInputSupersedeCandidatePreparerTestGrantResolver(AuthorityGrantResolution resolution) : IAuthorityGrantResolver
{
    internal AuthorityGrantResolution Resolution { get; set; } = resolution;
    internal Exception? ResolveException { get; set; }

    internal bool DelayResolveUntilCancellation { get; set; }

    internal TaskCompletionSource<bool>? ResolveEntered { get; set; }

    public async Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
    {
        ResolveEntered?.TrySetResult(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (DelayResolveUntilCancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (ResolveException is not null)
        {
            throw ResolveException;
        }

        return Resolution;
    }
}

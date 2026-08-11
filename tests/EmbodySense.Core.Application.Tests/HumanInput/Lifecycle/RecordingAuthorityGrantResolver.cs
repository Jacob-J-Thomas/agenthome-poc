using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

internal sealed class RecordingAuthorityGrantResolver(AuthorityGrantResolution resolution) : IAuthorityGrantResolver
{
    internal List<(AuthorityGrantReference? Reference, CancellationToken CancellationToken)> Calls { get; } = [];

    internal Func<AuthorityGrantReference?, CancellationToken, AuthorityGrantResolution>? Handler { get; set; }

    public Task<AuthorityGrantResolution> ResolveAsync(
        AuthorityGrantReference? reference,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((reference, cancellationToken));
        return Task.FromResult(Handler?.Invoke(reference, cancellationToken) ?? resolution);
    }
}

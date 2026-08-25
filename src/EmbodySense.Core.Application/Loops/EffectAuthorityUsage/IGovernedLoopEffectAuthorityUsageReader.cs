using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage;

/// <summary>Reads the canonical completion-consumption posture of one exact immutable authority grant.</summary>
public interface IGovernedLoopEffectAuthorityUsageReader
{
    /// <summary>Returns whether first-bound-run completion evidence leaves the exact grant usable.</summary>
    /// <param name="grant">The exact immutable grant revision and content hash to inspect.</param>
    /// <param name="cancellationToken">The token used while authenticating the canonical usage evidence.</param>
    /// <returns>A fail-closed completion-consumption posture for the exact grant.</returns>
    Task<GovernedLoopEffectAuthorityGrantUsageReadResult> ReadCompletionUsageAsync(
        AuthorityGrantReference? grant,
        CancellationToken cancellationToken = default);
}

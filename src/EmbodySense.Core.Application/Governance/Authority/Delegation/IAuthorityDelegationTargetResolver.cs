using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Delegation.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation;

/// <summary>Resolves only one caller-supplied exact role, loop, or node target and its current maxima.</summary>
public interface IAuthorityDelegationTargetResolver
{
    /// <summary>Resolves one exact immutable target without selecting a latest or successor target.</summary>
    Task<AuthorityDelegationTargetResolution> ResolveAsync(AuthorityDelegationTargetBinding target, CancellationToken cancellationToken = default);
}

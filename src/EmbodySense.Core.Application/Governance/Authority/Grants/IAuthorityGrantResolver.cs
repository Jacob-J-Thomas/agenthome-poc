using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Resolves one exact immutable grant revision without following replacements.</summary>
public interface IAuthorityGrantResolver
{
    /// <summary>Revalidates exact lifecycle, trusted time, profile, role, loop, owner, and ceiling posture.</summary>
    /// <param name="reference">The exact immutable grant reference.</param>
    /// <param name="cancellationToken">A token that cancels resolution.</param>
    /// <returns>An effective ceiling only when every exact check is active.</returns>
    Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default);
}

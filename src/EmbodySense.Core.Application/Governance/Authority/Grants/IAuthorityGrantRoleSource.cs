using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Resolves one exact contextual-role revision pin against current role lifecycle truth.</summary>
public interface IAuthorityGrantRoleSource
{
    /// <summary>Resolves one exact role revision without following a successor.</summary>
    /// <param name="pin">The exact role revision and canonical content hash.</param>
    /// <param name="cancellationToken">A token that cancels the source reads.</param>
    /// <returns>The exact current posture and value-free evidence digest.</returns>
    Task<AuthorityGrantRoleResolution> ResolveAsync(ContextualRoleRevisionPin? pin, CancellationToken cancellationToken = default);
}

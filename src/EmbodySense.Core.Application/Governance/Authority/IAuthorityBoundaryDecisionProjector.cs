using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority;

/// <summary>
/// Defines the surface-neutral boundary-decision projection port.
/// </summary>
public interface IAuthorityBoundaryDecisionProjector
{
    /// <summary>
    /// Projects an already evaluated boundary receipt without executing or approving an effect.
    /// </summary>
    /// <param name="receipt">The bounded authority boundary receipt.</param>
    /// <returns>The surface-neutral boundary projection.</returns>
    AuthorityBoundaryProjection Project(AuthorityBoundaryReceipt receipt);
}

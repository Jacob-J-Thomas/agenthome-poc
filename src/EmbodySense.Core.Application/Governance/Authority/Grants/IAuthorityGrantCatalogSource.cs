using EmbodySense.Core.Application.Governance.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Reads the bounded current authority-grant catalog for a server-owned scope filter.</summary>
/// <remarks>This query does not grant authority. Callers must filter and revalidate every returned exact grant before projection or use.</remarks>
public interface IAuthorityGrantCatalogSource
{
    /// <summary>Reads every current immutable grant revision in the bounded workspace ledger.</summary>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>Current exact grant revisions or a fail-closed availability posture.</returns>
    Task<AuthorityGrantCatalogReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default);
}

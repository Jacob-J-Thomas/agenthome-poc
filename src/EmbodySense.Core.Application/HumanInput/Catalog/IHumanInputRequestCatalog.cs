using EmbodySense.Core.Application.HumanInput.Catalog.Models;

namespace EmbodySense.Core.Application.HumanInput.Catalog;

/// <summary>Reads bounded canonical Human Input request posture from the authenticated workspace ledger.</summary>
/// <remarks>The catalog does not search by guessed request identifiers. Its cursor is an opaque continuation generated from
/// one exact authenticated ledger generation, and a changed generation fails closed instead of serving an unstable page.</remarks>
public interface IHumanInputRequestCatalog
{
    /// <summary>Reads one bounded stable page of canonical request posture.</summary>
    /// <param name="request">The requested page bound and optional opaque continuation cursor.</param>
    /// <param name="cancellationToken">A token that cancels the read before it completes.</param>
    /// <returns>A page, stale-cursor disposition, or fail-closed result.</returns>
    Task<HumanInputRequestCatalogPage> ListAsync(HumanInputRequestCatalogPageRequest? request, CancellationToken cancellationToken = default);

    /// <summary>Reads one exact canonical request aggregate without following lineage links.</summary>
    /// <param name="requestId">The exact stable request identifier.</param>
    /// <param name="cancellationToken">A token that cancels the read before it completes.</param>
    /// <returns>The exact aggregate or a fail-closed read result.</returns>
    Task<HumanInputRequestCatalogReadResult> ReadAsync(string requestId, CancellationToken cancellationToken = default);
}

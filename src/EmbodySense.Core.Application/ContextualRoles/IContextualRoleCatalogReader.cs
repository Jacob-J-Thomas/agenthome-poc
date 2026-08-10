using EmbodySense.Core.Application.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles;

/// <summary>Reads bounded deterministic pages of proved contextual-role state.</summary>
public interface IContextualRoleCatalogReader
{
    /// <summary>Reads one page without loading instruction content or granting authority.</summary>
    /// <param name="request">The bounded exclusive cursor and page size.</param>
    /// <param name="cancellationToken">A token that cancels the read before it completes.</param>
    /// <returns>The proved role page or one structured fail-closed outcome.</returns>
    Task<ContextualRoleCatalogReadResult> ReadCatalogAsync(ContextualRoleCatalogReadRequest request, CancellationToken cancellationToken = default);
}

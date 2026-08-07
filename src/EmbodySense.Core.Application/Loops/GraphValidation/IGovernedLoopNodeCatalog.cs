namespace EmbodySense.Core.Application.Loops.GraphValidation;

using EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Resolves the current Application-owned exact node descriptor catalog.</summary>
public interface IGovernedLoopNodeCatalog
{
    /// <summary>Gets one catalog snapshot for a deterministic validation decision.</summary>
    /// <param name="cancellationToken">Cancels snapshot resolution.</param>
    /// <returns>The current catalog snapshot. Unavailability must be explicit and never interpreted as executable support.</returns>
    Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

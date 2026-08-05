namespace EmbodySense.Core.Application.Loops.GraphValidation;

using EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Resolves current role authority for graph validation without granting authority itself.</summary>
public interface IGovernedLoopAuthoritySnapshotProvider
{
    /// <summary>Gets one current authority snapshot for the requested contextual role.</summary>
    /// <param name="roleId">The graph's owning role identity.</param>
    /// <param name="cancellationToken">Cancels snapshot resolution.</param>
    /// <returns>The current authority snapshot.</returns>
    Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(string roleId, CancellationToken cancellationToken = default);
}

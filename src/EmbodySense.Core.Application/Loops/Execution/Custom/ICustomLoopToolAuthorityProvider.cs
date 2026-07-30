using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Resolves current server-owned role authority against a run's admitted maximum.
/// </summary>
public interface ICustomLoopToolAuthorityProvider
{
    /// <summary>
    /// Computes the effective tool assignments and their evidence hashes.
    /// </summary>
    /// <param name="roleId">The role ID.</param>
    /// <param name="admittedMaximum">The admitted maximum.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The admitted maximum, current role ceiling, effective intersection, and authority evidence.</returns>
    Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default);
}

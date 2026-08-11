using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Startup.Workspace;

/// <summary>Seeds and verifies the exact non-granting contextual role owned by a fresh workspace.</summary>
public interface IDefaultContextualRoleSeeder
{
    /// <summary>Persists and verifies the default role after its registered instruction source has been scaffolded.</summary>
    /// <param name="paths">The canonical workspace paths.</param>
    /// <param name="cancellationToken">The token used to cancel persistence and verification.</param>
    /// <returns>The exact immutable role revision pin proved current and active.</returns>
    Task<ContextualRoleRevisionPin> SeedAsync(WorkspacePaths paths, CancellationToken cancellationToken = default);
}

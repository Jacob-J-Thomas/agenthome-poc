using System.Collections.Immutable;

namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Declares the workspace identifiers to which a role revision applies without resolving filesystem paths.</summary>
/// <param name="WorkspaceIds">The immutable explicit workspace identifiers.</param>
public sealed record ContextualRoleWorkspaceApplicability(ImmutableArray<string> WorkspaceIds)
{
    /// <summary>Determines whether the declared scope includes an exact workspace identifier.</summary>
    /// <param name="workspaceId">The workspace identifier to test.</param>
    /// <returns><see langword="true"/> only when an exact declared identifier matches.</returns>
    public bool AppliesTo(string workspaceId) => WorkspaceIds.Contains(workspaceId, StringComparer.Ordinal);
}

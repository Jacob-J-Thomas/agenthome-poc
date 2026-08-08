using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Common.ContextualRoles;

/// <summary>Provides exact workspace-membership checks for declarative contextual-role applicability.</summary>
public static class ContextualRoleWorkspaceApplicabilityExtensions
{
    /// <summary>Determines whether the declared scope includes an exact workspace identifier.</summary>
    /// <param name="applicability">The declarative workspace scope.</param>
    /// <param name="workspaceId">The workspace identifier to test.</param>
    /// <returns><see langword="true"/> only when an exact declared identifier matches.</returns>
    public static bool AppliesTo(this ContextualRoleWorkspaceApplicability applicability, string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(applicability);
        return applicability.WorkspaceIds.Contains(workspaceId, StringComparer.Ordinal);
    }
}

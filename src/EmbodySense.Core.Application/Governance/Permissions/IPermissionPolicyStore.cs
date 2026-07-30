using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Governance.Permissions;

/// <summary>
/// Loads the current permission policy and emits its canonical version-1 default document.
/// </summary>
public interface IPermissionPolicyStore
{
    /// <summary>
    /// Loads the workspace policy, returning a fail-closed policy when the document is missing or unsupported.
    /// </summary>
    /// <param name="paths">The resolved workspace paths containing the policy document.</param>
    /// <returns>The directory permission policy.</returns>
    IDirectoryPermissionPolicy Load(WorkspacePaths paths);

    /// <summary>
    /// Creates the canonical version-1 default policy JSON for a workspace.
    /// </summary>
    /// <param name="paths">The resolved workspace paths used to construct default rules.</param>
    /// <returns>The serialized policy document.</returns>
    string CreateDefaultJson(WorkspacePaths paths);
}

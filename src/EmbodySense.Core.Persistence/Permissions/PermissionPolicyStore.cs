using EmbodySense.Core.Common.Governance.Permissions;
using System.Text.Json;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Permissions;

/// <summary>
/// Loads the workspace permission document and projects it into the fail-closed directory policy.
/// </summary>
/// <remarks>
/// A missing, unreadable, or invalid document is treated as absent rather than partially trusted. Schema interpretation and
/// default-deny behavior belong to <see cref="DirectoryPermissionPolicy"/> and the version-1 permission document.
/// </remarks>
public sealed class PermissionPolicyStore : IPermissionPolicyStore
{
    /// <summary>
    /// Loads the current permission document, falling back to the policy's safe defaults when it cannot be trusted.
    /// </summary>
    /// <param name="paths">The workspace paths containing the optional permission document.</param>
    /// <returns>The directory permission policy.</returns>
    public IDirectoryPermissionPolicy Load(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return DirectoryPermissionPolicy.Create(paths, TryLoadDocument(paths));
    }

    /// <summary>
    /// Creates the canonical version-1 permission document for a newly initialized workspace.
    /// </summary>
    /// <param name="paths">The workspace paths whose canonical directories are embedded in the default policy.</param>
    /// <returns>The canonical version-1 permission JSON.</returns>
    public string CreateDefaultJson(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return PermissionsDocument.CreateDefault(paths).ToJson();
    }

    private static PermissionsDocument? TryLoadDocument(WorkspacePaths paths)
    {
        if (!File.Exists(paths.PermissionsPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(paths.PermissionsPath);
            return PermissionsDocument.FromJson(json);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

using System.Text.Json;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Permissions.Models;
using EmbodySense.Core.Persistence.Workspace.Models;

namespace EmbodySense.Core.Persistence.Permissions;

public sealed class PermissionPolicyStore : IPermissionPolicyStore
{
    public IDirectoryPermissionPolicy Load(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return DirectoryPermissionPolicy.Create(paths, TryLoadDocument(paths));
    }

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

using EmbodySense.Core.Startup.Workspace.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Permissions;

namespace EmbodySense.Core.Startup.Workspace;

/// <summary>
/// Projects workspace paths and fail-closed directory permission policy into an interface-safe status snapshot.
/// </summary>
public sealed class WorkspaceStatusReader
{
    /// <summary>
    /// Reads current workspace initialization and permission status without modifying the workspace.
    /// </summary>
    /// <param name="rootPath">The workspace root, normalized to an absolute path.</param>
    /// <returns>
    /// A snapshot whose initialized flag requires the <c>.agent</c> directory, a readable nonblank role
    /// document, and a valid current-version permissions document. Missing, invalid, or unsupported permission
    /// configuration is represented as approval-required default access.
    /// </returns>
    public WorkspaceStatusSnapshot Read(string rootPath)
    {
        var paths = new WorkspacePaths(rootPath);
        var permissions = new PermissionPolicyStore().Load(paths);
        var isInitialized = Directory.Exists(paths.AgentPath) && IsRoleDocumentAvailable(paths.RolePath) && permissions.HasDocument;

        return new WorkspaceStatusSnapshot(
            RootPath: paths.RootPath,
            AgentPath: paths.AgentPath,
            WorkspacePath: paths.WorkspacePath,
            IsInitialized: isInitialized,
            HasPartialScaffold: Directory.Exists(paths.AgentPath) && !isInitialized,
            EventsLogPath: paths.EventsLogPath,
            PermissionsPath: paths.PermissionsPath,
            TasksPath: paths.TasksPath,
            DefaultAccess: FormatDefaultAccess(permissions),
            ApprovedEntries: FormatApprovedEntries(permissions.Approved),
            DeniedEntries: FormatDeniedEntries(permissions.Denied));
    }

    private static bool IsRoleDocumentAvailable(string rolePath)
    {
        if (!File.Exists(rolePath))
        {
            return false;
        }

        try
        {
            using var reader = File.OpenText(rolePath);
            while (reader.Read() is var character && character >= 0)
            {
                if (!char.IsWhiteSpace((char)character))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static string FormatDefaultAccess(IDirectoryPermissionPolicy permissions)
    {
        return permissions.HasDocument ? "requires approval for missing or unmatched directory rules" : "requires approval because permissions.json is missing, invalid, or unsupported";
    }

    private static IReadOnlyList<string> FormatApprovedEntries(IReadOnlyList<ApprovedFileSystemPermission> entries)
    {
        return entries.Select(entry => $"{entry.Path} [{FormatOperations(entry.Operations)}]{FormatApproval(entry)}").ToArray();
    }

    private static IReadOnlyList<string> FormatDeniedEntries(IReadOnlyList<DeniedFileSystemPermission> entries)
    {
        return entries.Select(entry => $"{entry.Path} [{FormatOperations(entry.Operations)}]").ToArray();
    }

    private static string FormatOperations(IReadOnlyList<FileSystemOperation> operations)
    {
        return string.Join("/", operations.Select(operation => operation.ToString().ToLowerInvariant()));
    }

    private static string FormatApproval(ApprovedFileSystemPermission entry)
    {
        return entry.RequiresApproval ? " (approval required)" : "";
    }
}

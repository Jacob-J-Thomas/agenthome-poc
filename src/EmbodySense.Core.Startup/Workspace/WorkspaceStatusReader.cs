using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Permissions;

namespace EmbodySense.Core.Startup.Workspace;

public sealed class WorkspaceStatusReader
{
    public WorkspaceStatusSnapshot Read(string rootPath)
    {
        var paths = new WorkspacePaths(rootPath);
        var permissions = new PermissionPolicyStore().Load(paths);

        return new WorkspaceStatusSnapshot(
            RootPath: paths.RootPath,
            AgentPath: paths.AgentPath,
            WorkspacePath: paths.WorkspacePath,
            IsInitialized: paths.IsInitialized,
            EventsLogPath: paths.EventsLogPath,
            PermissionsPath: paths.PermissionsPath,
            TasksPath: paths.TasksPath,
            DefaultAccess: FormatDefaultAccess(permissions),
            ApprovedEntries: FormatApprovedEntries(permissions.Approved),
            DeniedEntries: FormatDeniedEntries(permissions.Denied));
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

using EmbodySense.Cli.Command.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Permissions.Models;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.Workspace.Models;

namespace EmbodySense.Cli.Command;

public static class StatusCommand
{
    public static int Run(CliArguments arguments)
    {
        var root = arguments.At(1) ?? Directory.GetCurrentDirectory();
        var paths = new WorkspacePaths(root);

        Console.WriteLine($"Root:          {paths.RootPath}");
        Console.WriteLine($"Agent path:    {paths.AgentPath}");
        Console.WriteLine($"Workspace:     {paths.WorkspacePath}");
        Console.WriteLine($"Initialized:   {paths.IsInitialized}");
        Console.WriteLine($"Audit log:     {paths.EventsLogPath}");
        Console.WriteLine($"Permissions:   {paths.PermissionsPath}");
        Console.WriteLine($"Tasks path:    {paths.TasksPath}");

        var permissions = new PermissionPolicyStore().Load(paths);
        Console.WriteLine($"Default access: {FormatDefaultAccess(permissions)}");
        Console.WriteLine($"Approved:       {FormatApprovedEntries(permissions.Approved)}");
        Console.WriteLine($"Denied:         {FormatDeniedEntries(permissions.Denied)}");

        return paths.IsInitialized ? 0 : 2;
    }

    private static string FormatDefaultAccess(IDirectoryPermissionPolicy permissions)
    {
        return permissions.HasDocument ? "requires approval for missing or unmatched directory rules" : "requires approval because permissions.json is missing, invalid, or unsupported";
    }

    private static string FormatApprovedEntries(IReadOnlyList<ApprovedFileSystemPermission> entries)
    {
        return entries.Count == 0 ? "(none)" : string.Join(", ", entries.Select(entry => $"{entry.Path} [{FormatOperations(entry.Operations)}]{FormatApproval(entry)}"));
    }

    private static string FormatDeniedEntries(IReadOnlyList<DeniedFileSystemPermission> entries)
    {
        return entries.Count == 0 ? "(none)" : string.Join(", ", entries.Select(entry => $"{entry.Path} [{FormatOperations(entry.Operations)}]"));
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

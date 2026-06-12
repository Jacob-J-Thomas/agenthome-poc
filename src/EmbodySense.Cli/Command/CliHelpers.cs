using EmbodySense.Cli.Permissions;
using EmbodySense.Cli.Workspace;

namespace EmbodySense.Cli.Command;

internal static class CliHelpers
{
    public static void PrintHelp()
    {
        Console.WriteLine("""
            EmbodySense POC CLI

            usage:
              embodysense init [root]
              embodysense run [--model model] [--workdir path]
              embodysense status [root]
              embodysense audit [tail] [root] [--limit count]

            example:
              embodysense init ./scratch
              embodysense run
              embodysense audit tail ./scratch --limit 10
            """);
    }

    public static async Task<int> InitAsync(CliArguments arguments)
    {
        var root = arguments.At(1) ?? Directory.GetCurrentDirectory();
        var initializer = new WorkspaceInitializer();
        await initializer.InitializeAsync(root);
        var paths = new WorkspacePaths(root);
        Console.WriteLine($"Initialized EmbodySense workspace at {Path.GetFullPath(root)}");
        Console.WriteLine($"Permissions: {paths.PermissionsPath}");
        return 0;
    }

    public static int Status(CliArguments arguments)
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

        var permissions = DirectoryPermissionPolicy.Load(paths);
        Console.WriteLine($"Default access: {FormatDefaultAccess(permissions)}");
        Console.WriteLine($"Approved:       {FormatApprovedEntries(permissions.Approved)}");
        Console.WriteLine($"Denied:         {FormatDeniedEntries(permissions.Denied)}");

        return paths.IsInitialized ? 0 : 2;
    }

    private static string FormatDefaultAccess(DirectoryPermissionPolicy permissions)
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

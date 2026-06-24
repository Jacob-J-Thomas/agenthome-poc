using EmbodySense.Cli.Command.Models;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Cli.Command;

public static class StatusCommand
{
    public static int Run(CliArguments arguments)
    {
        var root = arguments.At(1) ?? Directory.GetCurrentDirectory();
        var status = new WorkspaceStatusReader().Read(root);

        Console.WriteLine($"Root:          {status.RootPath}");
        Console.WriteLine($"Agent path:    {status.AgentPath}");
        Console.WriteLine($"Workspace:     {status.WorkspacePath}");
        Console.WriteLine($"Initialized:   {status.IsInitialized}");
        Console.WriteLine($"Audit log:     {status.EventsLogPath}");
        Console.WriteLine($"Permissions:   {status.PermissionsPath}");
        Console.WriteLine($"Tasks path:    {status.TasksPath}");
        Console.WriteLine($"Default access: {status.DefaultAccess}");
        Console.WriteLine($"Approved:       {FormatEntries(status.ApprovedEntries)}");
        Console.WriteLine($"Denied:         {FormatEntries(status.DeniedEntries)}");

        return status.IsInitialized ? 0 : 2;
    }

    private static string FormatEntries(IReadOnlyList<string> entries)
    {
        return entries.Count == 0 ? "(none)" : string.Join(", ", entries);
    }
}

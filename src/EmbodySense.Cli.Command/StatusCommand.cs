using EmbodySense.Cli.Command;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Cli.Command;

/// <summary>
/// Implements the CLI workspace-status projection.
/// </summary>
public static class StatusCommand
{
    /// <summary>
    /// Prints workspace layout and permission-policy status for an explicit operand or the current directory.
    /// </summary>
    /// <param name="arguments">The complete CLI token sequence, including the root <c>status</c> token.</param>
    /// <returns>Zero for an initialized workspace; two when initialization is still required.</returns>
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

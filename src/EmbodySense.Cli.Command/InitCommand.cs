using EmbodySense.Cli.Command.Models;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Cli.Command;

public static class InitCommand
{
    public static async Task<int> RunAsync(CliArguments arguments)
    {
        var root = arguments.At(1) ?? Directory.GetCurrentDirectory();
        await WorkspaceInitializer.ForCli().InitializeAsync(root);
        var status = new WorkspaceStatusReader().Read(root);
        Console.WriteLine($"Initialized EmbodySense workspace at {Path.GetFullPath(root)}");
        Console.WriteLine($"Permissions: {status.PermissionsPath}");
        return 0;
    }
}

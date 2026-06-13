using EmbodySense.Core.Workspace;

namespace EmbodySense.Cli.Command;

internal static class InitCommand
{
    public static async Task<int> RunAsync(CliArguments arguments)
    {
        var root = arguments.At(1) ?? Directory.GetCurrentDirectory();
        var initializer = new WorkspaceInitializer();
        await initializer.InitializeAsync(root);
        var paths = new WorkspacePaths(root);
        Console.WriteLine($"Initialized EmbodySense workspace at {Path.GetFullPath(root)}");
        Console.WriteLine($"Permissions: {paths.PermissionsPath}");
        return 0;
    }
}

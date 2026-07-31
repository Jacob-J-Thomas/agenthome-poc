using EmbodySense.Cli.Command;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Cli.Command;

/// <summary>
/// Implements explicit workspace initialization from the CLI.
/// </summary>
public static class InitCommand
{
    /// <summary>
    /// Initializes the requested operand or the current directory and prints the resulting paths.
    /// </summary>
    /// <param name="arguments">The complete CLI token sequence, including the root <c>init</c> token.</param>
    /// <returns>Zero after successful initialization.</returns>
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

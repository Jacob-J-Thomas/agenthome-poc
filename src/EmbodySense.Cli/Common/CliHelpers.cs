using EmbodySense.Cli.Workspace;

namespace EmbodySense.Cli.Common;

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

    public static async Task<int> InitAsync(string[] args)
    {
        var root = args.Length >= 2 ? args[1] : Directory.GetCurrentDirectory();
        var initializer = new WorkspaceInitializer();
        await initializer.InitializeAsync(root);
        Console.WriteLine($"Initialized EmbodySense workspace at {Path.GetFullPath(root)}");
        return 0;
    }

    public static int Status(string[] args)
    {
        var root = args.Length >= 2 ? args[1] : Directory.GetCurrentDirectory();
        var paths = new WorkspacePaths(root);

        Console.WriteLine($"Root:          {paths.RootPath}");
        Console.WriteLine($"Agent path:    {paths.AgentPath}");
        Console.WriteLine($"Workspace:     {paths.WorkspacePath}");
        Console.WriteLine($"Initialized:   {paths.IsInitialized}");
        Console.WriteLine($"Audit log:     {paths.EventsLogPath}");
        Console.WriteLine($"Tasks path:    {paths.TasksPath}");

        return paths.IsInitialized ? 0 : 2;
    }
}

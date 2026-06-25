using EmbodySense.Cli.Command.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Cli.Command;

public static class RunCommand
{
    public static async Task<int> RunAsync(CliArguments arguments)
    {
        var options = RunOptions.FromArguments(arguments);
        var status = new WorkspaceStatusReader().Read(options.WorkingDirectory);

        if (!status.IsInitialized)
        {
            if (!ConfirmWorkspaceInitialization(status))
            {
                Console.WriteLine("Workspace initialization cancelled. Run `embodysense init <root>` to initialize explicitly.");
                return 1;
            }

            await WorkspaceInitializer.ForCli().InitializeAsync(options.WorkingDirectory);
        }

        var client = ConsoleHarnessTerminal.Instance;
        await using var runtime = await new AgentRuntimeFactory(new ConsoleToolApprovalPrompt(client)).CreateAsync(options.Model, options.WorkingDirectory, options.CodexExecutablePath, options.CodexSandbox);
        return await runtime.RunConsoleLoopAsync(client, banner: Constants.Banner, verbose: options.Verbose);
    }

    private static bool ConfirmWorkspaceInitialization(WorkspaceStatusSnapshot status)
    {
        Console.WriteLine("Warning: this EmbodySense workspace is not initialized.");
        Console.WriteLine($"Root: {status.RootPath}");
        Console.WriteLine("Initializing will create .agent/ and workspace/ scaffolding with a default permissions policy.");
        Console.Write("Initialize this workspace now? [y/N] ");

        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}

using EmbodySense.Cli.Harness;
using EmbodySense.Cli.Command.Models;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Persistence.Workspace.Models;

namespace EmbodySense.Cli.Command;

internal static class RunCommand
{
    public static async Task<int> RunAsync(CliArguments arguments)
    {
        var options = RunOptions.FromArguments(arguments);
        var paths = new WorkspacePaths(options.WorkingDirectory);

        if (!paths.IsInitialized)
        {
            if (!ConfirmWorkspaceInitialization(paths))
            {
                Console.WriteLine("Workspace initialization cancelled. Run `embodysense init <root>` to initialize explicitly.");
                return 1;
            }

            await new WorkspaceInitializer().InitializeAsync(options.WorkingDirectory);
        }

        await using var runtime = await new AgentRuntimeFactory(new ConsoleToolApprovalPrompt()).CreateAsync(options.ToInferenceClientOptions());
        var commandHandler = new HarnessCommandHandler(runtime.ConversationMemory, runtime.StartupContext);

        return await AgentHarnessLoop.RunHarnessLoopAsync(runtime.Session, commandHandler);
    }

    private static bool ConfirmWorkspaceInitialization(WorkspacePaths paths)
    {
        Console.WriteLine("Warning: this EmbodySense workspace is not initialized.");
        Console.WriteLine($"Root: {paths.RootPath}");
        Console.WriteLine("Initializing will create .agent/ and workspace/ scaffolding with a default permissions policy.");
        Console.Write("Initialize this workspace now? [y/N] ");

        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}

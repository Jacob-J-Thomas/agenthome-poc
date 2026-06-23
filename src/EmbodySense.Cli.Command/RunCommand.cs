using EmbodySense.Cli.Command.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Harness;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Cli.Command;

public static class RunCommand
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

            await new WorkspaceInitializer(AuditSchema.Actors.Cli).InitializeAsync(options.WorkingDirectory);
        }

        var client = ConsoleHarnessTerminal.Instance;
        await using var runtime = await new AgentRuntimeFactory(new ConsoleToolApprovalPrompt(client)).CreateAsync(options.ToInferenceClientOptions());
        var commandHandler = new HarnessCommandHandler(client, runtime.ConversationMemory, runtime.StartupContext);
        var loopOptions = new AgentHarnessLoopOptions { Banner = Constants.Banner };

        return await AgentHarnessLoop.RunHarnessLoopAsync(runtime.Session, client, commandHandler, loopOptions);
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

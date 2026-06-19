using EmbodySense.Cli.Harness;
using EmbodySense.Cli.Command.Models;
using EmbodySense.Core.Context;
using EmbodySense.Core.Harness;
using EmbodySense.Core.Inference.Services;
using EmbodySense.Core.Memory;
using EmbodySense.Core.Permissions;
using EmbodySense.Core.Tools;
using EmbodySense.Core.Workspace;
using EmbodySense.Core.Workspace.Models;

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

        var permissionPolicy = DirectoryPermissionPolicy.Load(paths);
        var permissionService = new ToolPermissionService(paths, permissionPolicy);
        var toolBroker = new ToolBroker(paths, permissionService, new ConsoleToolApprovalPrompt());
        var conversationMemory = new ConversationMemoryStore(paths);
        var startupContext = await new AgentContextProvider().LoadAsync(paths);
        await conversationMemory.StartFreshConversationAsync();
        await using var inferenceClient = new LlmInferenceClient(options.ToInferenceClientOptions(), toolBroker);
        var session = new AgentHarnessSession(inferenceClient, conversationMemory, startupContext);
        var commandHandler = new HarnessCommandHandler(conversationMemory, startupContext);

        return await AgentHarnessLoop.RunHarnessLoopAsync(session, commandHandler);
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

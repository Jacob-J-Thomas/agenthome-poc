using EmbodySense.Cli.Harness;
using EmbodySense.Cli.Command.Models;
using EmbodySense.Core.Harness;
using EmbodySense.Core.Inference.Implementations;
using EmbodySense.Core.Permissions;
using EmbodySense.Core.Tools;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Cli.Command;

internal static class RunCommand
{
    public static async Task<int> RunAsync(CliArguments arguments)
    {
        var options = RunOptions.FromArguments(arguments);
        var paths = new WorkspacePaths(options.WorkingDirectory);
        var permissionPolicy = DirectoryPermissionPolicy.Load(paths);
        var permissionService = new ToolPermissionService(paths, permissionPolicy);
        var toolBroker = new ToolBroker(paths, permissionService, new ConsoleToolApprovalPrompt());
        await using var inferenceClient = new LlmInferenceClient(options.ToInferenceClientOptions(), toolBroker);
        var session = new AgentHarnessSession(inferenceClient);

        return await AgentHarnessLoop.RunHarnessLoopAsync(session);
    }
}

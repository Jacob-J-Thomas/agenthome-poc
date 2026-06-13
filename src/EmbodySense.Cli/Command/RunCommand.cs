using EmbodySense.Cli.Harness;
using EmbodySense.Core.Harness;
using EmbodySense.Core.Inference.Implementations;

namespace EmbodySense.Cli.Command;

internal static class RunCommand
{
    public static Task<int> RunAsync(CliArguments arguments)
    {
        var options = RunOptions.FromArguments(arguments);
        var inferenceClient = new LlmInferenceClient(options.ToInferenceClientOptions());
        var session = new AgentHarnessSession(inferenceClient);

        return AgentHarnessLoop.RunHarnessLoopAsync(session);
    }
}

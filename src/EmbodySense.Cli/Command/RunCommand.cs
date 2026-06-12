using EmbodySense.Cli.Harness;

namespace EmbodySense.Cli.Command;

internal static class RunCommand
{
    public static Task<int> RunAsync(CliArguments arguments)
    {
        return AgentHarnessLoop.RunHarnessLoopAsync(arguments);
    }
}

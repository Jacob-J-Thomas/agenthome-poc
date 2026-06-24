namespace EmbodySense.Core.Startup.Runtime;

public sealed record AgentRuntimeCommandResult(
    bool Handled,
    string Output,
    bool ExitRequested = false)
{
    public static AgentRuntimeCommandResult NotHandled { get; } = new(false, string.Empty);

    public static AgentRuntimeCommandResult HandledOutput(string output)
    {
        return new AgentRuntimeCommandResult(true, output);
    }

    public static AgentRuntimeCommandResult HandledExit()
    {
        return new AgentRuntimeCommandResult(true, string.Empty, ExitRequested: true);
    }
}

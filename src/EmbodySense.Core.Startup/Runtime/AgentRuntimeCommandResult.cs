namespace EmbodySense.Core.Startup.Runtime;

public sealed record AgentRuntimeCommandResult
{
    public AgentRuntimeCommandResult(
        bool handled,
        string output,
        bool exitRequested = false,
        IReadOnlyList<AgentRuntimeTranscriptMessage>? restoredMessages = null,
        bool replaceTranscript = false)
    {
        Handled = handled;
        Output = output;
        ExitRequested = exitRequested;
        RestoredMessages = restoredMessages ?? [];
        ReplaceTranscript = replaceTranscript;
    }

    public bool Handled { get; }

    public string Output { get; }

    public bool ExitRequested { get; }

    public IReadOnlyList<AgentRuntimeTranscriptMessage> RestoredMessages { get; }

    public bool ReplaceTranscript { get; }

    public static AgentRuntimeCommandResult NotHandled { get; } = new(false, string.Empty);

    public static AgentRuntimeCommandResult HandledOutput(string output)
    {
        return new AgentRuntimeCommandResult(true, output);
    }

    public static AgentRuntimeCommandResult HandledExit()
    {
        return new AgentRuntimeCommandResult(true, string.Empty, exitRequested: true);
    }
}

public sealed record AgentRuntimeTranscriptMessage(string Role, string Content);

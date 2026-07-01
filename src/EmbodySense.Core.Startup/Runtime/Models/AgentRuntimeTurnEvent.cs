namespace EmbodySense.Core.Startup.Runtime.Models;

public sealed record AgentRuntimeTurnEvent
{
    private AgentRuntimeTurnEvent(
        AgentRuntimeTurnEventKind kind,
        string text = "",
        IReadOnlyList<AgentRuntimeTranscriptMessage>? transcriptMessages = null,
        AgentRuntimeRunIdentity? runIdentity = null)
    {
        if (!Enum.IsDefined(kind) || kind == AgentRuntimeTurnEventKind.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Choose a concrete runtime turn event kind.");
        }

        Kind = kind;
        Text = text;
        TranscriptMessages = transcriptMessages ?? [];
        RunIdentity = runIdentity;
    }

    public AgentRuntimeTurnEventKind Kind { get; }

    public string Text { get; }

    public IReadOnlyList<AgentRuntimeTranscriptMessage> TranscriptMessages { get; }

    public AgentRuntimeRunIdentity? RunIdentity { get; }

    public static AgentRuntimeTurnEvent CommandOutput(string text)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.CommandOutput, text);
    }

    public static AgentRuntimeTurnEvent Prompt(string text)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.Prompt, text);
    }

    public static AgentRuntimeTurnEvent TranscriptReplacement(IReadOnlyList<AgentRuntimeTranscriptMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.TranscriptReplacement, transcriptMessages: messages);
    }

    public static AgentRuntimeTurnEvent AssistantMessage(string text, AgentRuntimeRunIdentity? runIdentity = null)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.AssistantMessage, text, runIdentity: runIdentity);
    }

    public static AgentRuntimeTurnEvent Failure(string text, AgentRuntimeRunIdentity? runIdentity = null)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.Failure, text, runIdentity: runIdentity);
    }

    public static AgentRuntimeTurnEvent Cancellation(string text, AgentRuntimeRunIdentity? runIdentity = null)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.Cancellation, text, runIdentity: runIdentity);
    }

    public static AgentRuntimeTurnEvent ExitRequested()
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.ExitRequested);
    }
}

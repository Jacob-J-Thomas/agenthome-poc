using EmbodySense.Core.Startup.Runtime.Models;
namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Represents one ordered, surface-neutral event emitted by a runtime turn.
/// </summary>
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

    /// <summary>
    /// Gets the event discriminator that determines which payload fields are meaningful.
    /// </summary>
    public AgentRuntimeTurnEventKind Kind { get; }

    /// <summary>
    /// Gets command, prompt, assistant, failure, or cancellation text; otherwise, an empty string.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the replacement transcript for <see cref="AgentRuntimeTurnEventKind.TranscriptReplacement"/>; otherwise, an empty list.
    /// </summary>
    public IReadOnlyList<AgentRuntimeTranscriptMessage> TranscriptMessages { get; }

    /// <summary>
    /// Gets the durable loop/run/role attribution for a model event, when available.
    /// </summary>
    public AgentRuntimeRunIdentity? RunIdentity { get; }

    /// <summary>
    /// Creates a command-output event.
    /// </summary>
    /// <param name="text">The interface-ready command output.</param>
    /// <returns>A command-output event.</returns>
    public static AgentRuntimeTurnEvent CommandOutput(string text)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.CommandOutput, text);
    }

    /// <summary>
    /// Creates an input-prompt event.
    /// </summary>
    /// <param name="text">The prompt to present to the user.</param>
    /// <returns>A prompt event.</returns>
    public static AgentRuntimeTurnEvent Prompt(string text)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.Prompt, text);
    }

    /// <summary>
    /// Creates an event that atomically replaces the interface transcript projection.
    /// </summary>
    /// <param name="messages">The complete ordered replacement transcript.</param>
    /// <returns>A transcript-replacement event.</returns>
    public static AgentRuntimeTurnEvent TranscriptReplacement(IReadOnlyList<AgentRuntimeTranscriptMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.TranscriptReplacement, transcriptMessages: messages);
    }

    /// <summary>
    /// Creates a completed assistant-message event.
    /// </summary>
    /// <param name="text">The accepted assistant text.</param>
    /// <param name="runIdentity">Optional durable run attribution.</param>
    /// <returns>An assistant-message event.</returns>
    public static AgentRuntimeTurnEvent AssistantMessage(string text, AgentRuntimeRunIdentity? runIdentity = null)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.AssistantMessage, text, runIdentity: runIdentity);
    }

    /// <summary>
    /// Creates a terminal failure event.
    /// </summary>
    /// <param name="text">The failure detail safe to project to the interface.</param>
    /// <param name="runIdentity">Optional durable run attribution.</param>
    /// <returns>A failure event.</returns>
    public static AgentRuntimeTurnEvent Failure(string text, AgentRuntimeRunIdentity? runIdentity = null)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.Failure, text, runIdentity: runIdentity);
    }

    /// <summary>
    /// Creates a terminal review-required event.
    /// </summary>
    public static AgentRuntimeTurnEvent NeedsReview(string text, AgentRuntimeRunIdentity? runIdentity = null)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.NeedsReview, text, runIdentity: runIdentity);
    }

    /// <summary>
    /// Creates a terminal cancellation event.
    /// </summary>
    /// <param name="text">The cancellation detail safe to project to the interface.</param>
    /// <param name="runIdentity">Optional durable run attribution.</param>
    /// <returns>A cancellation event.</returns>
    public static AgentRuntimeTurnEvent Cancellation(string text, AgentRuntimeRunIdentity? runIdentity = null)
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.Cancellation, text, runIdentity: runIdentity);
    }

    /// <summary>
    /// Creates an event requesting that the hosting interface end its session.
    /// </summary>
    /// <returns>An exit-requested event with no additional payload.</returns>
    public static AgentRuntimeTurnEvent ExitRequested()
    {
        return new AgentRuntimeTurnEvent(AgentRuntimeTurnEventKind.ExitRequested);
    }
}

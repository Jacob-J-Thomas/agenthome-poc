namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>
/// Identifies the supported agent runtime turn event kind values.
/// </summary>
public enum AgentRuntimeTurnEventKind
{
    /// <summary>
    /// No concrete event kind has been selected.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Text emitted by a handled runtime command.
    /// </summary>
    CommandOutput,
    /// <summary>
    /// A follow-up prompt that expects user input.
    /// </summary>
    Prompt,
    /// <summary>
    /// A complete transcript that replaces the interface projection.
    /// </summary>
    TranscriptReplacement,
    /// <summary>
    /// Accepted assistant output from a model turn.
    /// </summary>
    AssistantMessage,
    /// <summary>
    /// Terminal model-turn failure detail.
    /// </summary>
    Failure,
    /// <summary>
    /// Terminal provider ambiguity that requires explicit human review.
    /// </summary>
    NeedsReview,
    /// <summary>
    /// Terminal model-turn cancellation detail.
    /// </summary>
    Cancellation,
    /// <summary>
    /// A request for the hosting interface to end its session.
    /// </summary>
    ExitRequested
}

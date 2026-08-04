using EmbodySense.Core.Startup.Runtime.Models;
namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Projects one command or model turn into stable status, transcript, and ordered interface events.
/// </summary>
public sealed record AgentRuntimeTurnResult
{
    private AgentRuntimeTurnResult(AgentRuntimeTurnStatus status)
    {
        if (!Enum.IsDefined(status) || status == AgentRuntimeTurnStatus.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Choose a concrete runtime turn status.");
        }

        Status = status;
    }

    /// <summary>
    /// Gets the concrete terminal status of the handled turn.
    /// </summary>
    public AgentRuntimeTurnStatus Status { get; }

    /// <summary>
    /// Gets the primary command, assistant, failure, or cancellation text.
    /// </summary>
    public string Output { get; private init; } = string.Empty;

    /// <summary>
    /// Gets the follow-up prompt requested by a handled command, when one exists.
    /// </summary>
    public string? Prompt { get; private init; }

    /// <summary>
    /// Gets a value indicating whether the runtime command is waiting for another user response.
    /// </summary>
    public bool AwaitingInput { get; private init; }

    /// <summary>
    /// Gets a value indicating whether the hosting interface should end the session.
    /// </summary>
    public bool ExitRequested => Status == AgentRuntimeTurnStatus.ExitRequested;

    /// <summary>
    /// Gets a value indicating whether a model turn completed successfully.
    /// </summary>
    public bool IsMessageTurn => Status == AgentRuntimeTurnStatus.MessageCompleted;

    /// <summary>
    /// Gets a value indicating whether a model turn failed.
    /// </summary>
    public bool IsFailure => Status == AgentRuntimeTurnStatus.MessageFailed;

    /// <summary>
    /// Gets a value indicating whether a provider outcome requires explicit human review.
    /// </summary>
    public bool NeedsReview => Status == AgentRuntimeTurnStatus.MessageNeedsReview;

    /// <summary>
    /// Gets a value indicating whether a model turn was cancelled.
    /// </summary>
    public bool IsCancelled => Status == AgentRuntimeTurnStatus.MessageCancelled;

    /// <summary>
    /// Gets the complete restored transcript supplied by a history-loading command.
    /// </summary>
    public IReadOnlyList<AgentRuntimeTranscriptMessage> RestoredMessages { get; private init; } = [];

    /// <summary>
    /// Gets a value indicating whether the interface should replace, rather than append to, its transcript.
    /// </summary>
    public bool ReplaceTranscript { get; private init; }

    /// <summary>
    /// Gets the durable loop/run/role attribution for a model result, when available.
    /// </summary>
    public AgentRuntimeRunIdentity? RunIdentity { get; private init; }

    /// <summary>
    /// Gets the failure or cancellation detail for unsuccessful model turns.
    /// </summary>
    public string? FailureDetail { get; private init; }

    /// <summary>
    /// Gets the ordered, normalized events the hosting interface should apply.
    /// </summary>
    public IReadOnlyList<AgentRuntimeTurnEvent> Events { get; private init; } = [];

    /// <summary>
    /// Creates a handled-command result and derives its ordered interface events.
    /// </summary>
    /// <param name="output">The command output, which may be empty.</param>
    /// <param name="prompt">An optional follow-up prompt.</param>
    /// <param name="awaitingInput">Whether the command expects a subsequent user response.</param>
    /// <param name="restoredMessages">An optional complete restored transcript.</param>
    /// <param name="replaceTranscript">Whether <paramref name="restoredMessages"/> replaces the interface transcript.</param>
    /// <returns>A command-handled result with transcript replacement, output, and prompt events in that order.</returns>
    public static AgentRuntimeTurnResult CommandOutput(
        string output,
        string? prompt = null,
        bool awaitingInput = false,
        IReadOnlyList<AgentRuntimeTranscriptMessage>? restoredMessages = null,
        bool replaceTranscript = false)
    {
        var events = BuildCommandEvents(output, prompt, restoredMessages, replaceTranscript);
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.CommandHandled)
        {
            Output = output,
            Prompt = prompt,
            AwaitingInput = awaitingInput,
            RestoredMessages = restoredMessages ?? [],
            ReplaceTranscript = replaceTranscript,
            Events = events
        };
    }

    /// <summary>
    /// Creates a handled result that requests interface shutdown.
    /// </summary>
    /// <returns>An exit-requested result.</returns>
    public static AgentRuntimeTurnResult Exit()
    {
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.ExitRequested)
        {
            Events = [AgentRuntimeTurnEvent.ExitRequested()]
        };
    }

    /// <summary>
    /// Creates a successful model-turn result.
    /// </summary>
    /// <param name="output">The nonblank accepted assistant output.</param>
    /// <param name="runIdentity">Optional durable run attribution.</param>
    /// <returns>A completed result containing one assistant-message event.</returns>
    public static AgentRuntimeTurnResult MessageCompleted(string output, AgentRuntimeRunIdentity? runIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.MessageCompleted)
        {
            Output = output,
            RunIdentity = runIdentity,
            Events = [AgentRuntimeTurnEvent.AssistantMessage(output, runIdentity)]
        };
    }

    /// <summary>
    /// Creates a failed model-turn result while preserving any assistant events accepted before the failure.
    /// </summary>
    /// <param name="failureDetail">The nonblank terminal failure detail.</param>
    /// <param name="runIdentity">Optional durable run attribution.</param>
    /// <param name="priorEvents">Assistant events already accepted before the failure.</param>
    /// <returns>A failed result whose final event is the failure.</returns>
    public static AgentRuntimeTurnResult MessageFailed(
        string failureDetail,
        AgentRuntimeRunIdentity? runIdentity = null,
        IReadOnlyList<AgentRuntimeTurnEvent>? priorEvents = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDetail);
        var events = new List<AgentRuntimeTurnEvent>(priorEvents ?? []);
        events.Add(AgentRuntimeTurnEvent.Failure(failureDetail, runIdentity));
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.MessageFailed)
        {
            Output = failureDetail,
            RunIdentity = runIdentity,
            FailureDetail = failureDetail,
            Events = events
        };
    }

    /// <summary>
    /// Creates a review-required model-turn result while preserving accepted assistant events.
    /// </summary>
    public static AgentRuntimeTurnResult MessageNeedsReview(
        string detail,
        AgentRuntimeRunIdentity? runIdentity = null,
        IReadOnlyList<AgentRuntimeTurnEvent>? priorEvents = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var events = new List<AgentRuntimeTurnEvent>(priorEvents ?? []);
        events.Add(AgentRuntimeTurnEvent.NeedsReview(detail, runIdentity));
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.MessageNeedsReview)
        {
            Output = detail,
            RunIdentity = runIdentity,
            FailureDetail = detail,
            Events = events
        };
    }

    /// <summary>
    /// Creates a cancelled model-turn result.
    /// </summary>
    /// <param name="detail">The nonblank terminal cancellation detail.</param>
    /// <param name="runIdentity">Optional durable run attribution.</param>
    /// <returns>A cancelled result containing one cancellation event.</returns>
    public static AgentRuntimeTurnResult MessageCancelled(string detail, AgentRuntimeRunIdentity? runIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.MessageCancelled)
        {
            Output = detail,
            RunIdentity = runIdentity,
            FailureDetail = detail,
            Events = [AgentRuntimeTurnEvent.Cancellation(detail, runIdentity)]
        };
    }

    private static IReadOnlyList<AgentRuntimeTurnEvent> BuildCommandEvents(
        string output,
        string? prompt,
        IReadOnlyList<AgentRuntimeTranscriptMessage>? restoredMessages,
        bool replaceTranscript)
    {
        var events = new List<AgentRuntimeTurnEvent>();
        if (replaceTranscript)
        {
            events.Add(AgentRuntimeTurnEvent.TranscriptReplacement(restoredMessages ?? []));
        }

        if (!string.IsNullOrEmpty(output))
        {
            events.Add(AgentRuntimeTurnEvent.CommandOutput(output));
        }

        if (!string.IsNullOrEmpty(prompt))
        {
            events.Add(AgentRuntimeTurnEvent.Prompt(prompt));
        }

        return events;
    }
}

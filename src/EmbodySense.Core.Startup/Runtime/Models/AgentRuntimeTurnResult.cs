namespace EmbodySense.Core.Startup.Runtime.Models;

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

    public AgentRuntimeTurnStatus Status { get; }

    public string Output { get; private init; } = string.Empty;

    public string? Prompt { get; private init; }

    public bool AwaitingInput { get; private init; }

    public bool ExitRequested => Status == AgentRuntimeTurnStatus.ExitRequested;

    public bool IsMessageTurn => Status == AgentRuntimeTurnStatus.MessageCompleted;

    public bool IsFailure => Status == AgentRuntimeTurnStatus.MessageFailed;

    public bool IsCancelled => Status == AgentRuntimeTurnStatus.MessageCancelled;

    public IReadOnlyList<AgentRuntimeTranscriptMessage> RestoredMessages { get; private init; } = [];

    public bool ReplaceTranscript { get; private init; }

    public AgentRuntimeRunIdentity? RunIdentity { get; private init; }

    public string? FailureDetail { get; private init; }

    public IReadOnlyList<AgentRuntimeTurnEvent> Events { get; private init; } = [];

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

    public static AgentRuntimeTurnResult Exit()
    {
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.ExitRequested)
        {
            Events = [AgentRuntimeTurnEvent.ExitRequested()]
        };
    }

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

    public static AgentRuntimeTurnResult MessageFailed(string failureDetail, AgentRuntimeRunIdentity? runIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDetail);
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.MessageFailed)
        {
            Output = failureDetail,
            RunIdentity = runIdentity,
            FailureDetail = failureDetail,
            Events = [AgentRuntimeTurnEvent.Failure(failureDetail, runIdentity)]
        };
    }

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

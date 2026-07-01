namespace EmbodySense.Core.Startup.Runtime.Models;

public sealed record AgentRuntimeTurnResult
{
    private AgentRuntimeTurnResult(
        AgentRuntimeTurnStatus status,
        string output,
        string? prompt = null,
        bool awaitingInput = false,
        IReadOnlyList<AgentRuntimeTranscriptMessage>? restoredMessages = null,
        bool replaceTranscript = false,
        AgentRuntimeRunIdentity? runIdentity = null,
        string? failureDetail = null)
    {
        if (!Enum.IsDefined(status) || status == AgentRuntimeTurnStatus.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Choose a concrete runtime turn status.");
        }

        Output = output;
        Status = status;
        Prompt = prompt;
        AwaitingInput = awaitingInput;
        RestoredMessages = restoredMessages ?? [];
        ReplaceTranscript = replaceTranscript;
        RunIdentity = runIdentity;
        FailureDetail = failureDetail;
    }

    public AgentRuntimeTurnStatus Status { get; }

    public string Output { get; }

    public string? Prompt { get; }

    public bool AwaitingInput { get; }

    public bool ExitRequested => Status == AgentRuntimeTurnStatus.ExitRequested;

    public bool IsMessageTurn => Status == AgentRuntimeTurnStatus.MessageCompleted;

    public bool IsFailure => Status == AgentRuntimeTurnStatus.MessageFailed;

    public bool IsCancelled => Status == AgentRuntimeTurnStatus.MessageCancelled;

    public IReadOnlyList<AgentRuntimeTranscriptMessage> RestoredMessages { get; }

    public bool ReplaceTranscript { get; }

    public AgentRuntimeRunIdentity? RunIdentity { get; }

    public string? FailureDetail { get; }

    public static AgentRuntimeTurnResult CommandOutput(
        string output,
        string? prompt = null,
        bool awaitingInput = false,
        IReadOnlyList<AgentRuntimeTranscriptMessage>? restoredMessages = null,
        bool replaceTranscript = false)
    {
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.CommandHandled, output, prompt, awaitingInput, restoredMessages, replaceTranscript);
    }

    public static AgentRuntimeTurnResult Exit()
    {
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.ExitRequested, string.Empty);
    }

    public static AgentRuntimeTurnResult MessageCompleted(string output, AgentRuntimeRunIdentity? runIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.MessageCompleted, output, runIdentity: runIdentity);
    }

    public static AgentRuntimeTurnResult MessageFailed(string failureDetail, AgentRuntimeRunIdentity? runIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDetail);
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.MessageFailed, failureDetail, runIdentity: runIdentity, failureDetail: failureDetail);
    }

    public static AgentRuntimeTurnResult MessageCancelled(string detail, AgentRuntimeRunIdentity? runIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new AgentRuntimeTurnResult(AgentRuntimeTurnStatus.MessageCancelled, detail, runIdentity: runIdentity, failureDetail: detail);
    }
}

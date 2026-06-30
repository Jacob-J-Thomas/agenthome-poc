using EmbodySense.Core.Application.Runtime;

namespace EmbodySense.Core.Application.Loops.Execution;

public sealed record DefaultConversationLoopTurnResult
{
    // TODO(default-loop-result-shape): Replace this wide private constructor with clearer outcome-specific result models or a smaller validated factory shape.
    // Deferred until run identity, host-exit behavior, and failure recovery are real; revisit when LoopRunIdentity is wired into persisted loop/audit records.
    private DefaultConversationLoopTurnResult(
        DefaultConversationLoopTurnStatus status,
        string assistantOutput,
        IReadOnlyList<RuntimeTranscriptMessage> transcriptMessages,
        LoopRunIdentity? runIdentity,
        string? failureDetail,
        bool exitRequested)
    {
        if (!Enum.IsDefined(status) || status == DefaultConversationLoopTurnStatus.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Choose a concrete turn status.");
        }

        if (status == DefaultConversationLoopTurnStatus.Completed)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assistantOutput);
        }

        if (status == DefaultConversationLoopTurnStatus.Failed)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureDetail);
        }

        Status = status;
        AssistantOutput = assistantOutput;
        TranscriptMessages = transcriptMessages;
        RunIdentity = runIdentity;
        FailureDetail = failureDetail;
        ExitRequested = exitRequested;
    }

    public DefaultConversationLoopTurnStatus Status { get; }

    public string AssistantOutput { get; }

    public IReadOnlyList<RuntimeTranscriptMessage> TranscriptMessages { get; }

    public LoopRunIdentity? RunIdentity { get; }

    public string? FailureDetail { get; }

    public bool ExitRequested { get; }

    public static DefaultConversationLoopTurnResult Completed(
        string assistantOutput,
        IReadOnlyList<RuntimeTranscriptMessage>? transcriptMessages = null,
        LoopRunIdentity? runIdentity = null)
    {
        return new DefaultConversationLoopTurnResult(DefaultConversationLoopTurnStatus.Completed, assistantOutput, transcriptMessages ?? [], runIdentity, null, exitRequested: false);
    }

    public static DefaultConversationLoopTurnResult Failed(
        string failureDetail,
        IReadOnlyList<RuntimeTranscriptMessage>? transcriptMessages = null,
        LoopRunIdentity? runIdentity = null)
    {
        return new DefaultConversationLoopTurnResult(DefaultConversationLoopTurnStatus.Failed, string.Empty, transcriptMessages ?? [], runIdentity, failureDetail, exitRequested: false);
    }

    public static DefaultConversationLoopTurnResult Cancelled(
        string? detail = null,
        IReadOnlyList<RuntimeTranscriptMessage>? transcriptMessages = null,
        LoopRunIdentity? runIdentity = null)
    {
        return new DefaultConversationLoopTurnResult(DefaultConversationLoopTurnStatus.Cancelled, string.Empty, transcriptMessages ?? [], runIdentity, detail, exitRequested: false);
    }

    // TODO(default-loop-host-exit): Either wire host-exit into the shared runtime turn API or remove this unused outcome from the loop result contract.
    // Deferred because current Web/CLI exit handling still lives above the loop; revisit when AgentRuntime exposes one host-facing event model.
    public static DefaultConversationLoopTurnResult HostExitRequested()
    {
        return new DefaultConversationLoopTurnResult(DefaultConversationLoopTurnStatus.Cancelled, string.Empty, [], null, "Turn host requested exit.", exitRequested: true);
    }
}

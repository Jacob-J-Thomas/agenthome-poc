using EmbodySense.Core.Application.Runtime.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Models;

public sealed record DefaultConversationLoopTurnResult
{
    private DefaultConversationLoopTurnResult(
        DefaultConversationLoopTurnStatus status,
        IReadOnlyList<RuntimeTranscriptMessage>? transcriptMessages = null,
        LoopRunIdentity? runIdentity = null,
        bool userMessageAccepted = false)
    {
        if (!Enum.IsDefined(status) || status == DefaultConversationLoopTurnStatus.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Choose a concrete turn status.");
        }

        Status = status;
        TranscriptMessages = transcriptMessages ?? [];
        RunIdentity = runIdentity;
        UserMessageAccepted = userMessageAccepted;
    }

    public DefaultConversationLoopTurnStatus Status { get; }

    public string AssistantOutput { get; private init; } = string.Empty;

    public IReadOnlyList<RuntimeTranscriptMessage> TranscriptMessages { get; }

    public LoopRunIdentity? RunIdentity { get; }

    public bool UserMessageAccepted { get; }

    public string? FailureDetail { get; private init; }

    public static DefaultConversationLoopTurnResult Completed(
        string assistantOutput,
        IReadOnlyList<RuntimeTranscriptMessage>? transcriptMessages = null,
        LoopRunIdentity? runIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantOutput);
        return new DefaultConversationLoopTurnResult(DefaultConversationLoopTurnStatus.Completed, transcriptMessages, runIdentity, userMessageAccepted: true)
        {
            AssistantOutput = assistantOutput
        };
    }

    public static DefaultConversationLoopTurnResult Failed(
        string failureDetail,
        IReadOnlyList<RuntimeTranscriptMessage>? transcriptMessages = null,
        LoopRunIdentity? runIdentity = null,
        bool userMessageAccepted = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDetail);
        return new DefaultConversationLoopTurnResult(DefaultConversationLoopTurnStatus.Failed, transcriptMessages, runIdentity, userMessageAccepted)
        {
            FailureDetail = failureDetail
        };
    }

    public static DefaultConversationLoopTurnResult Cancelled(
        string? detail = null,
        IReadOnlyList<RuntimeTranscriptMessage>? transcriptMessages = null,
        LoopRunIdentity? runIdentity = null,
        bool userMessageAccepted = false)
    {
        return new DefaultConversationLoopTurnResult(DefaultConversationLoopTurnStatus.Cancelled, transcriptMessages, runIdentity, userMessageAccepted)
        {
            FailureDetail = detail
        };
    }
}

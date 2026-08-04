using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Application.Runtime.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

/// <summary>
/// Represents a default conversation loop turn result.
/// </summary>
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

    /// <summary>
    /// Gets the default conversation loop turn status.
    /// </summary>
    /// <value>The default conversation loop turn status.</value>
    public DefaultConversationLoopTurnStatus Status { get; }

    /// <summary>
    /// Gets the assistant output.
    /// </summary>
    /// <value>The assistant output.</value>
    public string AssistantOutput { get; private init; } = string.Empty;

    /// <summary>
    /// Gets the runtime transcript messages.
    /// </summary>
    /// <value>The runtime transcript messages.</value>
    public IReadOnlyList<RuntimeTranscriptMessage> TranscriptMessages { get; }

    /// <summary>
    /// Gets the loop run identity.
    /// </summary>
    /// <value>The loop run identity.</value>
    public LoopRunIdentity? RunIdentity { get; }

    /// <summary>
    /// Gets a value indicating whether the user message accepted condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the user message accepted condition holds; otherwise, <see langword="false"/>.</value>
    public bool UserMessageAccepted { get; }

    /// <summary>
    /// Gets the failure detail.
    /// </summary>
    /// <value>The failure detail.</value>
    public string? FailureDetail { get; private init; }

    /// <summary>
    /// Creates a default conversation loop turn result representing completed.
    /// </summary>
    /// <param name="assistantOutput">The assistant output.</param>
    /// <param name="transcriptMessages">The transcript messages.</param>
    /// <param name="runIdentity">The run identity.</param>
    /// <returns>The default conversation loop turn result.</returns>
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

    /// <summary>
    /// Creates a default conversation loop turn result representing failed.
    /// </summary>
    /// <param name="failureDetail">The failure detail.</param>
    /// <param name="transcriptMessages">The transcript messages.</param>
    /// <param name="runIdentity">The run identity.</param>
    /// <param name="userMessageAccepted">The user message accepted.</param>
    /// <returns>The default conversation loop turn result.</returns>
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

    /// <summary>
    /// Creates a cancelled turn result.
    /// </summary>
    /// <param name="detail">The detail.</param>
    /// <param name="transcriptMessages">The transcript messages.</param>
    /// <param name="runIdentity">The run identity.</param>
    /// <param name="userMessageAccepted">The user message accepted.</param>
    /// <returns>The default conversation loop turn result.</returns>
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

    /// <summary>
    /// Creates a terminal turn result that forbids automatic redispatch and requires explicit reconciliation.
    /// </summary>
    /// <param name="detail">The actionable ambiguity or conflict evidence.</param>
    /// <param name="transcriptMessages">Messages whose durable publication was proved.</param>
    /// <param name="runIdentity">The stable loop-run identity.</param>
    /// <param name="userMessageAccepted">Whether the exact user message was durably accepted.</param>
    /// <returns>A needs-review result.</returns>
    public static DefaultConversationLoopTurnResult NeedsReview(
        string detail,
        IReadOnlyList<RuntimeTranscriptMessage>? transcriptMessages = null,
        LoopRunIdentity? runIdentity = null,
        bool userMessageAccepted = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new DefaultConversationLoopTurnResult(DefaultConversationLoopTurnStatus.NeedsReview, transcriptMessages, runIdentity, userMessageAccepted)
        {
            FailureDetail = detail
        };
    }
}

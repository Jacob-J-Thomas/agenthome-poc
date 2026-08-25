using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.HumanReview.Models;
namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Represents a custom loop run event.
/// </summary>
/// <param name="Sequence">The sequence.</param>
/// <param name="EventId">The event ID.</param>
/// <param name="TimestampUtc">The UTC event time.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Iteration">The iteration.</param>
/// <param name="StepId">The step ID.</param>
/// <param name="Attempt">The attempt.</param>
/// <param name="Detail">The detail.</param>
/// <param name="ContextBlocks">The context blocks.</param>
/// <param name="CanonicalOutput">The canonical output.</param>
/// <param name="OriginalOutputCharacterCount">The original output character count.</param>
/// <param name="CanonicalOutputTruncated">The canonical output truncated.</param>
/// <param name="RetainedForLoopReasoning">The retained for loop reasoning.</param>
/// <param name="PublishedToInvokingConversation">The published to invoking conversation.</param>
/// <param name="ConversationPublicationId">The conversation publication ID.</param>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="ProviderResponseId">The provider response ID.</param>
/// <param name="ExitDecision">The exit decision.</param>
/// <param name="ToolAuthority">The tool authority.</param>
/// <param name="ToolEvidence">The tool evidence.</param>
/// <param name="TraceReservationUtf8Bytes">The trace reservation UTF-8 bytes.</param>
/// <param name="ControlExpectedLifecycleVersion">The control expected lifecycle version.</param>
public sealed record CustomLoopRunEvent(
    long Sequence,
    string EventId,
    DateTimeOffset TimestampUtc,
    CustomLoopRunEventKind Kind,
    int? Iteration,
    string? StepId,
    int? Attempt,
    string Detail,
    CustomLoopContextBlock[] ContextBlocks,
    string? CanonicalOutput,
    int? OriginalOutputCharacterCount,
    bool? CanonicalOutputTruncated,
    bool? RetainedForLoopReasoning,
    bool? PublishedToInvokingConversation,
    string? ConversationPublicationId,
    string? Provider,
    string? Model,
    string? ProviderResponseId,
    CustomLoopExitDecision? ExitDecision,
    CustomLoopToolAuthoritySnapshot? ToolAuthority = null,
    CustomLoopToolTraceEvidence? ToolEvidence = null,
    int? TraceReservationUtf8Bytes = null,
    int? ControlExpectedLifecycleVersion = null)
{
    /// <summary>Gets exact canonical sequential-node dispatch or outcome evidence, or null for legacy-only events.</summary>
    [JsonRequired]
    public CustomLoopSequentialNodeEvidence? SequentialNodeEvidence { get; init; }

    /// <summary>Gets the bounded canonical pure-node outcome JSON, or null when the event does not complete a Transform or Validate node.</summary>
    /// <remarks>The Application boundary verifies this retained text against the event's exact immutable graph revision before execution can resume.</remarks>
    [JsonRequired]
    public string? PureNodeOutcomeJson { get; init; }

    /// <summary>Gets the exact resumed Wait continuation hash consumed by this completion event, or null for every other event.</summary>
    /// <remarks>The JSON property is required even when null so schema-1 run artifacts cannot silently omit this evidence plane.</remarks>
    [JsonRequired]
    public string? WaitContinuationEvidenceHash { get; init; }

    /// <summary>Gets exact reconciled model-profile, reservation, and usage evidence for a completed provider attempt.</summary>
    /// <remarks>The JSON property is required even when null so schema-1 artifacts cannot silently omit this evidence plane.</remarks>
    [JsonRequired]
    public GovernedModelAttemptExecutionEvidence? ModelExecutionEvidence { get; init; }

    /// <summary>Gets the exact immutable classified failure artifact carried by this failed or review-blocked node outcome.</summary>
    /// <remarks>The JSON property is required even when null so schema-1 run artifacts cannot omit failure classification posture.</remarks>
    [JsonRequired]
    public GovernedLoopFailureEvidence? FailureEvidence { get; init; }

    /// <summary>Gets one exact append-only durable retry-series state version, or null for every non-retry event.</summary>
    /// <remarks>The JSON property is required even when null so schema-1 run artifacts cannot omit retry posture.</remarks>
    [JsonRequired]
    public GovernedLoopRetryState? RetryState { get; init; }

    /// <summary>Gets typed Human Review evidence for one review event, or null for every non-review event.</summary>
    /// <remarks>The JSON property is required even when null so schema-1 artifacts cannot omit the review-evidence plane.</remarks>
    [JsonRequired]
    public HumanReviewEvidence? HumanReviewEvidence { get; init; }

}

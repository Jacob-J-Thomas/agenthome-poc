namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects one ordered append-only event from a custom-loop run trace.
/// </summary>
/// <param name="Sequence">The sequence.</param>
/// <param name="EventId">The event identifier.</param>
/// <param name="TimestampUtc">The timestamp utc.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Iteration">The iteration.</param>
/// <param name="StepId">The step identifier.</param>
/// <param name="Attempt">The attempt.</param>
/// <param name="Detail">The detail.</param>
/// <param name="ContextBlocks">The context blocks.</param>
/// <param name="CanonicalOutput">The canonical output.</param>
/// <param name="OriginalOutputCharacterCount">The original output character count.</param>
/// <param name="CanonicalOutputTruncated">The canonical output truncated.</param>
/// <param name="RetainedForLoopReasoning">The retained for loop reasoning.</param>
/// <param name="PublishedToInvokingConversation">The published to invoking conversation.</param>
/// <param name="ConversationPublicationId">The conversation publication identifier.</param>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="ProviderResponseId">The provider response identifier.</param>
/// <param name="ExitDecision">The exit decision.</param>
/// <param name="ToolAuthority">The tool authority.</param>
/// <param name="ToolEvidence">The tool evidence.</param>
public sealed record LoopRunEventSnapshot(
    long Sequence,
    string EventId,
    DateTimeOffset TimestampUtc,
    string Kind,
    int? Iteration,
    string? StepId,
    int? Attempt,
    string Detail,
    IReadOnlyList<LoopRunContextBlockSnapshot> ContextBlocks,
    string? CanonicalOutput,
    int? OriginalOutputCharacterCount,
    bool? CanonicalOutputTruncated,
    bool? RetainedForLoopReasoning,
    bool? PublishedToInvokingConversation,
    string? ConversationPublicationId,
    string? Provider,
    string? Model,
    string? ProviderResponseId,
    string? ExitDecision,
    LoopRunToolAuthoritySnapshot? ToolAuthority,
    LoopRunToolEvidenceSnapshot? ToolEvidence);

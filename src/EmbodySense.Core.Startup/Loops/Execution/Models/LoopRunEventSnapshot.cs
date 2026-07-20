namespace EmbodySense.Core.Startup.Loops.Execution;

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

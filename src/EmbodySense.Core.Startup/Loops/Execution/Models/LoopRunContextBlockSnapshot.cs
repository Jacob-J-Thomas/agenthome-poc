namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunContextBlockSnapshot(
    string Source,
    string SourceId,
    string Role,
    bool Included,
    string? OmissionReason,
    string Content,
    string ContentHash,
    int CharacterCount,
    bool Truncated,
    string? SourceVersion);

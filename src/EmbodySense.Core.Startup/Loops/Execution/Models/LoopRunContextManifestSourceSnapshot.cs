namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunContextManifestSourceSnapshot(
    int Order,
    string SourceType,
    string SourceId,
    string SourcePath,
    string Provenance,
    string TrustClass,
    string Role,
    string Content,
    string ContentHash,
    int OriginalCharacterCount,
    int UsedCharacterCount,
    bool Truncated,
    string? TruncationReason,
    string? OmissionReason,
    DateTimeOffset CapturedAtUtc);

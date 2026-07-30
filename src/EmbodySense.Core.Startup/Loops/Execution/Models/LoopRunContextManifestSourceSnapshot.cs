namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Describes one content-hashed, provenance-classified source in the admitted context manifest.
/// </summary>
/// <param name="Order">The order.</param>
/// <param name="SourceType">The source type.</param>
/// <param name="SourceId">The source identifier.</param>
/// <param name="SourcePath">The source path.</param>
/// <param name="Provenance">The provenance.</param>
/// <param name="TrustClass">The trust class.</param>
/// <param name="Role">The role.</param>
/// <param name="Content">The content.</param>
/// <param name="ContentHash">The content hash.</param>
/// <param name="OriginalCharacterCount">The original character count.</param>
/// <param name="UsedCharacterCount">The used character count.</param>
/// <param name="Truncated">The truncated.</param>
/// <param name="TruncationReason">The truncation reason.</param>
/// <param name="OmissionReason">The omission reason.</param>
/// <param name="CapturedAtUtc">The captured at utc.</param>
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

using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Represents a custom loop context block.
/// </summary>
/// <param name="Source">The source.</param>
/// <param name="SourceId">The stable source identifier.</param>
/// <param name="Role">The model message role assigned to the content.</param>
/// <param name="Included">The included.</param>
/// <param name="OmissionReason">The omission reason, or <see langword="null"/> when the source was included.</param>
/// <param name="Content">The exact content.</param>
/// <param name="ContentHash">The lowercase SHA-256 digest of the exact content.</param>
/// <param name="CharacterCount">The character count.</param>
/// <param name="Truncated">Whether content was truncated to a configured limit.</param>
/// <param name="SourceVersion">The source version.</param>
public sealed record CustomLoopContextBlock(
    CustomLoopContextSource Source,
    string SourceId,
    LlmMessageRole Role,
    bool Included,
    string? OmissionReason,
    string Content,
    string ContentHash,
    int CharacterCount,
    bool Truncated,
    string? SourceVersion = null);

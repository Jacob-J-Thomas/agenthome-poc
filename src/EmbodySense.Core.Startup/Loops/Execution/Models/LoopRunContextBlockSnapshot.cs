namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects one role, trigger, conversation, retained-output, or iteration-result context block.
/// </summary>
/// <param name="Source">The source.</param>
/// <param name="SourceId">The source identifier.</param>
/// <param name="Role">The role.</param>
/// <param name="Included">The included.</param>
/// <param name="OmissionReason">The omission reason.</param>
/// <param name="Content">The content.</param>
/// <param name="ContentHash">The content hash.</param>
/// <param name="CharacterCount">The character count.</param>
/// <param name="Truncated">The truncated.</param>
/// <param name="SourceVersion">The source version.</param>
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

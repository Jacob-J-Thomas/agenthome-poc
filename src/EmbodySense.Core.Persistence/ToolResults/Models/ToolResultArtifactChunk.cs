namespace EmbodySense.Core.Persistence.ToolResults.Models;

/// <summary>
/// Represents a tool result artifact chunk.
/// </summary>
/// <param name="Sequence">The sequence.</param>
/// <param name="Path">The path.</param>
/// <param name="ContentSha256">The content SHA-256.</param>
/// <param name="CharacterCount">The character count.</param>
/// <param name="Utf8ByteCount">The UTF-8 byte count.</param>
internal sealed record ToolResultArtifactChunk(
    int Sequence,
    string Path,
    string ContentSha256,
    int CharacterCount,
    long Utf8ByteCount);

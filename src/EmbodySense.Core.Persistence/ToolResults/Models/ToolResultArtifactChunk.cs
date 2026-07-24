namespace EmbodySense.Core.Persistence.ToolResults.Models;

internal sealed record ToolResultArtifactChunk(
    int Sequence,
    string Path,
    string ContentSha256,
    int CharacterCount,
    long Utf8ByteCount);

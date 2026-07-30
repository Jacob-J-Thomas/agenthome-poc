namespace EmbodySense.Core.Persistence.ToolResults.Models;

/// <summary>
/// Represents a prepared chunk.
/// </summary>
/// <param name="Path">The path.</param>
/// <param name="Bytes">The bytes.</param>
/// <param name="Manifest">The manifest.</param>
internal sealed record PreparedChunk(string Path, byte[] Bytes, ToolResultArtifactChunk Manifest);

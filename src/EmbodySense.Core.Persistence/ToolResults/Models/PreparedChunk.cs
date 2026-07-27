namespace EmbodySense.Core.Persistence.ToolResults.Models;

internal sealed record PreparedChunk(string Path, byte[] Bytes, ToolResultArtifactChunk Manifest);

namespace EmbodySense.Core.Persistence.ToolResults.Models;

internal sealed record PreparedArtifact(ToolResultArtifactManifest Manifest, byte[] ManifestBytes, IReadOnlyList<PreparedChunk> Chunks, long TotalUtf8Bytes);

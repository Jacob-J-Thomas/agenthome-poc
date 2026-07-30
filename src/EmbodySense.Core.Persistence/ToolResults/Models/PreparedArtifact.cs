namespace EmbodySense.Core.Persistence.ToolResults.Models;

/// <summary>
/// Represents a prepared artifact.
/// </summary>
/// <param name="Manifest">The manifest.</param>
/// <param name="ManifestBytes">The manifest bytes.</param>
/// <param name="Chunks">The chunks.</param>
/// <param name="TotalUtf8Bytes">The total UTF-8 bytes.</param>
internal sealed record PreparedArtifact(ToolResultArtifactManifest Manifest, byte[] ManifestBytes, IReadOnlyList<PreparedChunk> Chunks, long TotalUtf8Bytes);

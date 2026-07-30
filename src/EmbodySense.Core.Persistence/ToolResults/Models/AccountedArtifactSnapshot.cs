namespace EmbodySense.Core.Persistence.ToolResults.Models;

/// <summary>
/// Represents an accounted artifact snapshot.
/// </summary>
/// <param name="Manifest">The manifest.</param>
/// <param name="TotalUtf8Bytes">The total UTF-8 bytes.</param>
/// <param name="Files">The files.</param>
internal sealed record AccountedArtifactSnapshot(ToolResultArtifactManifest Manifest, long TotalUtf8Bytes, IReadOnlyDictionary<string, ArtifactFileStamp> Files);

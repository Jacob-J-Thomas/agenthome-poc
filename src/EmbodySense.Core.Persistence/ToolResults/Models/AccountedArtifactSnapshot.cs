namespace EmbodySense.Core.Persistence.ToolResults.Models;

/// <summary>
/// Represents an accounted artifact snapshot.
/// </summary>
/// <param name="Manifest">The manifest.</param>
/// <param name="TotalUtf8Bytes">The total UTF-8 bytes.</param>
/// <param name="ManifestFileSha256">The exact SHA-256 of the bounded manifest file used for accounting.</param>
/// <param name="Files">The files.</param>
internal sealed record AccountedArtifactSnapshot(ToolResultArtifactManifest Manifest, long TotalUtf8Bytes, string ManifestFileSha256, IReadOnlyDictionary<string, ArtifactFileStamp> Files);

namespace EmbodySense.Core.Persistence.ToolResults.Models;

internal sealed record AccountedArtifactSnapshot(ToolResultArtifactManifest Manifest, long TotalUtf8Bytes, IReadOnlyDictionary<string, ArtifactFileStamp> Files);

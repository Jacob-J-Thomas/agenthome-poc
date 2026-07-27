namespace EmbodySense.Core.Persistence.ToolResults.Models;

internal sealed class RetainedArtifact(string directory, ToolResultArtifactManifest manifest, long totalUtf8Bytes, bool contentValidated)
{
    public string Directory { get; } = directory;

    public ToolResultArtifactManifest Manifest { get; } = manifest;

    public long TotalUtf8Bytes { get; } = totalUtf8Bytes;

    public bool ContentValidated { get; set; } = contentValidated;

    public bool Evicted { get; set; }
}

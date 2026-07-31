namespace EmbodySense.Core.Persistence.ToolResults.Models;

/// <summary>
/// Represents a retained artifact.
/// </summary>
/// <param name="directory">The directory.</param>
/// <param name="manifest">The manifest.</param>
/// <param name="totalUtf8Bytes">The total UTF-8 bytes.</param>
/// <param name="contentValidated">The content validated.</param>
internal sealed class RetainedArtifact(string directory, ToolResultArtifactManifest manifest, long totalUtf8Bytes, bool contentValidated)
{
    /// <summary>
    /// Gets the directory.
    /// </summary>
    /// <value>The directory.</value>
    public string Directory { get; } = directory;

    /// <summary>
    /// Gets the tool result artifact manifest.
    /// </summary>
    /// <value>The tool result artifact manifest.</value>
    public ToolResultArtifactManifest Manifest { get; } = manifest;

    /// <summary>
    /// Gets the total UTF-8 bytes.
    /// </summary>
    /// <value>The total UTF-8 bytes.</value>
    public long TotalUtf8Bytes { get; } = totalUtf8Bytes;

    /// <summary>
    /// Gets a value indicating whether the content validated condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the content validated condition holds; otherwise, <see langword="false"/>.</value>
    public bool ContentValidated { get; set; } = contentValidated;

    /// <summary>
    /// Gets a value indicating whether the evicted condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the evicted condition holds; otherwise, <see langword="false"/>.</value>
    public bool Evicted { get; set; }
}

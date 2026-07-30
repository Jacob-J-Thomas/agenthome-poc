namespace EmbodySense.Core.Persistence.ToolResults.Models;

/// <summary>
/// Represents an artifact file stamp.
/// </summary>
/// <param name="Length">The length.</param>
/// <param name="LastWriteTimeUtcTicks">The last write time UTC ticks.</param>
internal sealed record ArtifactFileStamp(long Length, long LastWriteTimeUtcTicks);

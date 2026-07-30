using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Governance.Permissions.Models;

/// <summary>
/// Represents a file system permission entry.
/// </summary>
public abstract class FileSystemPermissionEntry
{
    /// <summary>
    /// Gets the path.
    /// </summary>
    /// <value>The path.</value>
    [JsonPropertyOrder(0)]
    public string Path { get; init; } = "";

    /// <summary>
    /// Gets the file system operations.
    /// </summary>
    /// <value>The file system operations.</value>
    [JsonPropertyOrder(1)]
    public List<FileSystemOperation> Operations { get; init; } = [];
}

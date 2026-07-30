using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Governance.Permissions.Models;

/// <summary>
/// Represents an approved file system permission.
/// </summary>
public sealed class ApprovedFileSystemPermission : FileSystemPermissionEntry
{
    /// <summary>
    /// Gets a value indicating whether the value requires approval.
    /// </summary>
    /// <value><see langword="true"/> when the value requires approval; otherwise, <see langword="false"/>.</value>
    [JsonPropertyOrder(2)]
    public bool RequiresApproval { get; init; }
}

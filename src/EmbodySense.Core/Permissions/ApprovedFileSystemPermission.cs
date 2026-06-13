using System.Text.Json.Serialization;

namespace EmbodySense.Core.Permissions;

public sealed class ApprovedFileSystemPermission : FileSystemPermissionEntry
{
    [JsonPropertyOrder(2)]
    public bool RequiresApproval { get; init; }
}

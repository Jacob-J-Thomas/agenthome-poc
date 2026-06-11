using System.Text.Json.Serialization;

namespace EmbodySense.Cli.Permissions;

internal sealed class ApprovedFileSystemPermission : FileSystemPermissionEntry
{
    [JsonPropertyOrder(2)]
    public bool RequiresApproval { get; init; }
}

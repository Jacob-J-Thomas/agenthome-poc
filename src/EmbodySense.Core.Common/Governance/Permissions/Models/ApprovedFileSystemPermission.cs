using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Governance.Permissions.Models;

public sealed class ApprovedFileSystemPermission : FileSystemPermissionEntry
{
    [JsonPropertyOrder(2)]
    public bool RequiresApproval { get; init; }
}

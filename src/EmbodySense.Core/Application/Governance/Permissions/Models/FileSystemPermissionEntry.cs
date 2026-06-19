using System.Text.Json.Serialization;

namespace EmbodySense.Core.Application.Governance.Permissions.Models;

public abstract class FileSystemPermissionEntry
{
    [JsonPropertyOrder(0)]
    public string Path { get; init; } = "";

    [JsonPropertyOrder(1)]
    public List<FileSystemOperation> Operations { get; init; } = [];
}

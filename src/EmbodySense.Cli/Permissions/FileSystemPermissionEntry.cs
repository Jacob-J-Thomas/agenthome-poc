using System.Text.Json.Serialization;

namespace EmbodySense.Cli.Permissions;

internal abstract class FileSystemPermissionEntry
{
    [JsonPropertyOrder(0)]
    public string Path { get; init; } = "";

    [JsonPropertyOrder(1)]
    public List<FileSystemOperation> Operations { get; init; } = [];
}

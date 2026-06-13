using System.Text.Json;
using EmbodySense.Core.Workspace;

namespace EmbodySense.Core.Permissions;

internal sealed class PermissionsDocument
{
    public int Version { get; init; } = 2;

    public string Scope { get; init; } = "single-file-system-directory-level";

    public List<ApprovedFileSystemPermission> Approved { get; init; } = [];

    public List<DeniedFileSystemPermission> Denied { get; init; } = [];

    public static PermissionsDocument CreateDefault(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new PermissionsDocument
        {
            Version = 2,
            Scope = "single-file-system-directory-level",
            Approved =
            [
                new ApprovedFileSystemPermission { Path = "workspace/shared", Operations = StandardWritableOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = "workspace/generated", Operations = StandardWritableOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = "workspace/system", Operations = ReadOnlyOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = ".agent/tasks", Operations = StandardWritableOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = ".agent/exports", Operations = StandardWritableOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = ".agent/skills", Operations = ReadOnlyOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = ".agent/skills", Operations = MutableOperations(), RequiresApproval = true },
                new ApprovedFileSystemPermission { Path = ".agent/recipes", Operations = ReadOnlyOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = ".agent/recipes", Operations = MutableOperations(), RequiresApproval = true }
            ],
            Denied =
            [
                new DeniedFileSystemPermission { Path = "workspace/private", Operations = AllOperations() },
                new DeniedFileSystemPermission { Path = ".agent/audit", Operations = AllOperations() },
                new DeniedFileSystemPermission { Path = ".agent/logs", Operations = AllOperations() },
                new DeniedFileSystemPermission { Path = ".agent/hooks", Operations = AllOperations() }
            ]
        };
    }

    public static PermissionsDocument? TryLoad(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!File.Exists(paths.PermissionsPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(paths.PermissionsPath);
            var document = JsonSerializer.Deserialize<PermissionsDocument>(json, PermissionsJson.Options);
            return document is { Version: 2 } ? document : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, PermissionsJson.Options);

    private static List<FileSystemOperation> AllOperations()
    {
        return [FileSystemOperation.List, FileSystemOperation.Read, FileSystemOperation.Create, FileSystemOperation.Append, FileSystemOperation.Modify, FileSystemOperation.Delete];
    }

    private static List<FileSystemOperation> ReadOnlyOperations()
    {
        return [FileSystemOperation.List, FileSystemOperation.Read];
    }

    private static List<FileSystemOperation> MutableOperations()
    {
        return [FileSystemOperation.Create, FileSystemOperation.Append, FileSystemOperation.Modify];
    }

    private static List<FileSystemOperation> StandardWritableOperations()
    {
        return [FileSystemOperation.List, FileSystemOperation.Read, FileSystemOperation.Create, FileSystemOperation.Append, FileSystemOperation.Modify];
    }
}

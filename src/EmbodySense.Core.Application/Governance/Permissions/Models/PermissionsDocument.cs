using System.Text.Json;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Governance.Permissions.Models;

public sealed class PermissionsDocument
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
                new ApprovedFileSystemPermission { Path = "shared", Operations = StandardWritableOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = "generated", Operations = StandardWritableOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = "system", Operations = ReadOnlyOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = ".agent/tasks", Operations = StandardWritableOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = ".agent/exports", Operations = StandardWritableOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = ".agent/skills", Operations = ReadOnlyOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = ".agent/skills", Operations = MutableOperations(), RequiresApproval = true },
                new ApprovedFileSystemPermission { Path = ".agent/recipes", Operations = ReadOnlyOperations(), RequiresApproval = false },
                new ApprovedFileSystemPermission { Path = ".agent/recipes", Operations = MutableOperations(), RequiresApproval = true }
            ],
            Denied =
            [
                new DeniedFileSystemPermission { Path = "private", Operations = AllOperations() },
                new DeniedFileSystemPermission { Path = ".agent/audit", Operations = AllOperations() },
                new DeniedFileSystemPermission { Path = ".agent/logs", Operations = AllOperations() },
                new DeniedFileSystemPermission { Path = ".agent/hooks", Operations = AllOperations() }
            ]
        };
    }

    public static PermissionsDocument? FromJson(string json)
    {
        var document = JsonSerializer.Deserialize<PermissionsDocument>(json, PermissionsJson.Options);
        return document is { Version: 2 } ? document : null;
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

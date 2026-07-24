using System.Text.Json;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Common.Governance.Permissions.Models;

public sealed class PermissionsDocument
{
    public const int CurrentVersion = 3;
    public const int ToolResponseInspectionMigrationSourceVersion = 2;
    public const string ToolResponseInspectionPath = ".agent/logs/tool-responses";

    public int Version { get; init; } = CurrentVersion;

    public string Scope { get; init; } = "single-file-system-directory-level";

    public List<ApprovedFileSystemPermission> Approved { get; init; } = [];

    public List<DeniedFileSystemPermission> Denied { get; init; } = [];

    public static PermissionsDocument CreateDefault(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var document = new PermissionsDocument
        {
            Version = CurrentVersion,
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
        document.EnsureToolResponseInspectionApproval();
        return document;
    }

    public static PermissionsDocument? FromJson(string json)
    {
        var document = JsonSerializer.Deserialize<PermissionsDocument>(json, PermissionsJson.Options);
        return document is { Version: ToolResponseInspectionMigrationSourceVersion or CurrentVersion } ? document : null;
    }

    public string ToJson() => JsonSerializer.Serialize(this, PermissionsJson.Options);

    public PermissionsDocument? MigrateToolResponseInspectionPolicy()
    {
        if (Version != ToolResponseInspectionMigrationSourceVersion)
        {
            return null;
        }

        var migrated = new PermissionsDocument
        {
            Version = CurrentVersion,
            Scope = Scope,
            Approved = [.. Approved],
            Denied = [.. Denied]
        };
        migrated.EnsureToolResponseInspectionApproval();
        return migrated;
    }

    public bool EnsureToolResponseInspectionApproval()
    {
        var coveredOperations = Approved
            .Where(entry => PathEquals(entry.Path, ToolResponseInspectionPath))
            .SelectMany(entry => entry.Operations)
            .ToHashSet();
        var missingOperations = ReadOnlyOperations().Where(operation => !coveredOperations.Contains(operation)).ToList();
        if (missingOperations.Count == 0)
        {
            return false;
        }

        Approved.Add(new ApprovedFileSystemPermission
        {
            Path = ToolResponseInspectionPath,
            Operations = missingOperations,
            RequiresApproval = true
        });
        return true;
    }

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

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.Ordinal);
    }

    private static string NormalizePath(string value)
    {
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}

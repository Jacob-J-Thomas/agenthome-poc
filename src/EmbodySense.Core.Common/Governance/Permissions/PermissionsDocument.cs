using EmbodySense.Core.Common;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using System.Text.Json;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Common.Governance.Permissions;

/// <summary>
/// Defines the version-1 workspace file-system permission policy consumed by governed tool evaluation.
/// </summary>
public sealed class PermissionsDocument
{
    /// <summary>
    /// Only permissions-document version accepted by the current runtime.
    /// </summary>
    public const int CurrentVersion = 1;
    /// <summary>
    /// Workspace-relative retained-tool-response path that requires explicit read/list approval.
    /// </summary>
    public const string ToolResponseInspectionPath = ".agent/logs/tool-responses";

    /// <summary>
    /// Gets the persisted schema version.
    /// </summary>
    /// <value>The version.</value>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// Gets the policy scope identifier.
    /// </summary>
    /// <value>The scope.</value>
    public string Scope { get; init; } = "single-file-system-directory-level";

    /// <summary>
    /// Gets the approved file system permissions.
    /// </summary>
    /// <value>The approved file system permissions.</value>
    public List<ApprovedFileSystemPermission> Approved { get; init; } = [];

    /// <summary>
    /// Gets the denied file system permissions.
    /// </summary>
    /// <value>The denied file system permissions.</value>
    public List<DeniedFileSystemPermission> Denied { get; init; } = [];

    /// <summary>
    /// Creates the default least-authority workspace permission policy.
    /// </summary>
    /// <param name="paths">The canonical workspace paths used to anchor the policy.</param>
    /// <returns>A version-1 document that denies private, audit, log, and hook mutation; permits the standard writable surfaces; and requires approval to inspect retained tool responses.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="paths"/> is <see langword="null"/>.</exception>
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
        document.EnsureToolResponseInspectionApproval(paths);
        return document;
    }

    /// <summary>
    /// Deserializes a permissions document only when it declares the current schema version explicitly.
    /// </summary>
    /// <param name="json">The JSON document.</param>
    /// <returns>The deserialized current-version document, or <see langword="null"/> when the version is missing, malformed, duplicated, or unsupported.</returns>
    /// <exception cref="JsonException">Thrown when <paramref name="json"/> is not valid for the permissions schema.</exception>
    public static PermissionsDocument? FromJson(string json)
    {
        using var jsonDocument = JsonDocument.Parse(json);
        if (!HasExplicitCurrentVersion(jsonDocument.RootElement))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<PermissionsDocument>(json, PermissionsJson.Options);
        return document is { Version: CurrentVersion } ? document : null;
    }

    /// <summary>
    /// Serializes this document with the canonical permissions JSON options.
    /// </summary>
    /// <returns>The JSON representation.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, PermissionsJson.Options);

    /// <summary>
    /// Ensures retained tool-response read and list operations are covered only by exact-path rules that require human approval.
    /// </summary>
    /// <param name="paths">The canonical workspace paths used to resolve permission-rule paths.</param>
    /// <returns><see langword="true"/> when non-approval coverage was removed or approval-required coverage was added; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="paths"/> is <see langword="null"/>.</exception>
    public bool EnsureToolResponseInspectionApproval(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var inspectionOperations = ReadOnlyOperations();
        var inspectionOperationSet = inspectionOperations.ToHashSet();
        var emptiedEntries = new List<ApprovedFileSystemPermission>();
        var changed = false;
        foreach (var entry in Approved.Where(entry => !entry.RequiresApproval && PathEquals(paths.RootPath, entry.Path, ToolResponseInspectionPath)))
        {
            var removedOperations = entry.Operations.RemoveAll(inspectionOperationSet.Contains);
            if (removedOperations == 0)
            {
                continue;
            }

            changed = true;
            if (entry.Operations.Count == 0)
            {
                emptiedEntries.Add(entry);
            }
        }

        foreach (var entry in emptiedEntries)
        {
            Approved.Remove(entry);
        }

        var approvalCoveredOperations = Approved
            .Where(entry => entry.RequiresApproval && PathEquals(paths.RootPath, entry.Path, ToolResponseInspectionPath))
            .SelectMany(entry => entry.Operations)
            .ToHashSet();
        var missingOperations = inspectionOperations.Where(operation => !approvalCoveredOperations.Contains(operation)).ToList();
        if (missingOperations.Count == 0)
        {
            return changed;
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

    private static bool HasExplicitCurrentVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var foundVersion = false;
        foreach (var property in root.EnumerateObject().Where(property => string.Equals(property.Name, "version", StringComparison.OrdinalIgnoreCase)))
        {
            if (foundVersion || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var version) || version != CurrentVersion)
            {
                return false;
            }

            foundVersion = true;
        }

        return foundVersion;
    }

    private static bool PathEquals(string workspaceRootPath, string left, string right)
    {
        return string.Equals(ResolveRulePath(workspaceRootPath, left), ResolveRulePath(workspaceRootPath, right), FileSystemPathComparer.GetPathComparison());
    }

    private static string ResolveRulePath(string workspaceRootPath, string rulePath)
    {
        var effectiveRulePath = Path.IsPathRooted(rulePath) ? rulePath : Path.Combine(workspaceRootPath, rulePath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(effectiveRulePath));
    }
}

using EmbodySense.Core.Workspace;

namespace EmbodySense.Core.Permissions;

public sealed class DirectoryPermissionPolicy
{
    private readonly PermissionsDocument? _document;
    private readonly string _workspaceRootPath;

    private DirectoryPermissionPolicy(PermissionsDocument? document, string workspaceRootPath)
    {
        _document = document;
        _workspaceRootPath = Path.GetFullPath(workspaceRootPath);
    }

    public bool HasDocument => _document is not null;

    public IReadOnlyList<ApprovedFileSystemPermission> Approved => _document?.Approved ?? [];

    public IReadOnlyList<DeniedFileSystemPermission> Denied => _document?.Denied ?? [];

    public static DirectoryPermissionPolicy Load(WorkspacePaths paths)
    {
        var document = PermissionsDocument.TryLoad(paths);
        return new DirectoryPermissionPolicy(document, paths.RootPath);
    }

    public PermissionEvaluation EvaluateDirectory(string directoryPath, FileSystemOperation operation)
    {
        if (_document is null)
        {
            return PermissionEvaluation.RequiresApproval("", "permissions.json is missing, invalid, or unsupported.");
        }

        var candidatePath = Path.GetFullPath(directoryPath);
        var approvedMatch = FindBestMatch(_document.Approved, candidatePath, operation);
        var deniedMatch = FindBestMatch(_document.Denied, candidatePath, operation);

        if (deniedMatch is not null && (approvedMatch is null || deniedMatch.Specificity >= approvedMatch.Specificity))
        {
            return PermissionEvaluation.Denied(deniedMatch.Entry.Path);
        }

        if (approvedMatch?.Entry is ApprovedFileSystemPermission approvedEntry)
        {
            return approvedEntry.RequiresApproval ? PermissionEvaluation.RequiresApproval(approvedEntry.Path, "Approved directory rule requires human approval before use.") : PermissionEvaluation.Allowed(approvedEntry.Path);
        }

        return PermissionEvaluation.RequiresApproval("", "No approved or denied directory rule matched.");
    }

    public bool CanReadDirectory(string directoryPath) => EvaluateDirectory(directoryPath, FileSystemOperation.Read).Decision == PermissionDecision.Allow;

    public bool CanAppendDirectory(string directoryPath) => EvaluateDirectory(directoryPath, FileSystemOperation.Append).Decision == PermissionDecision.Allow;

    public bool CanModifyDirectory(string directoryPath) => EvaluateDirectory(directoryPath, FileSystemOperation.Modify).Decision == PermissionDecision.Allow;

    private PermissionRuleMatch? FindBestMatch<TEntry>(IReadOnlyList<TEntry> entries, string candidatePath, FileSystemOperation operation) where TEntry : FileSystemPermissionEntry
    {
        PermissionRuleMatch? bestMatch = null;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || !entry.Operations.Contains(operation))
            {
                continue;
            }

            var rulePath = ResolveRulePath(entry.Path);

            if (!IsWithinOrEqual(candidatePath, rulePath))
            {
                continue;
            }

            var specificity = rulePath.Length;

            if (bestMatch is null || specificity > bestMatch.Specificity)
            {
                bestMatch = new PermissionRuleMatch(entry, specificity);
            }
        }

        return bestMatch;
    }

    private string ResolveRulePath(string rulePath)
    {
        var effectiveRulePath = Path.IsPathRooted(rulePath) ? rulePath : Path.Combine(_workspaceRootPath, rulePath);
        return Path.GetFullPath(effectiveRulePath);
    }

    private static bool IsWithinOrEqual(string candidatePath, string allowedPath)
    {
        var normalizedCandidatePath = EnsureTrailingSeparator(candidatePath);
        var normalizedAllowedPath = EnsureTrailingSeparator(allowedPath);
        return normalizedCandidatePath.StartsWith(normalizedAllowedPath, GetPathComparison());
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}

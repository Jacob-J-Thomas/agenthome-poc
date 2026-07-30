using EmbodySense.Core.Common;
using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Governance.Permissions;

/// <summary>
/// Evaluates directory operations using the most-specific version-1 rule, deny-winning ties, and fail-closed defaults.
/// </summary>
/// <remarks>
/// Missing documents, unsupported schemas, and unmatched paths require human approval rather than granting implicit access.
/// Relative rule paths are resolved from the workspace root.
/// </remarks>
public sealed class DirectoryPermissionPolicy : IDirectoryPermissionPolicy
{
    private readonly PermissionsDocument? _document;
    private readonly string _workspaceRootPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectoryPermissionPolicy"/> type.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="workspaceRootPath">The workspace root path.</param>
    internal DirectoryPermissionPolicy(PermissionsDocument? document, string workspaceRootPath)
    {
        _document = document;
        _workspaceRootPath = Path.GetFullPath(workspaceRootPath);
    }

    /// <summary>
    /// Gets a value indicating whether a compatible permission document was loaded.
    /// </summary>
    /// <value><see langword="true"/> when a compatible document is available; otherwise, <see langword="false"/>.</value>
    public bool HasDocument => _document is not null;

    /// <summary>
    /// Gets the approved rules, or an empty list when no compatible document exists.
    /// </summary>
    /// <value>The approved file system permissions.</value>
    public IReadOnlyList<ApprovedFileSystemPermission> Approved => _document?.Approved ?? [];

    /// <summary>
    /// Gets the denied rules, or an empty list when no compatible document exists.
    /// </summary>
    /// <value>The denied file system permissions.</value>
    public IReadOnlyList<DeniedFileSystemPermission> Denied => _document?.Denied ?? [];

    /// <summary>
    /// Creates a policy rooted at the supplied workspace.
    /// </summary>
    /// <param name="paths">The resolved workspace paths used for relative rules.</param>
    /// <param name="document">The compatible document, or <see langword="null"/> for a fail-closed policy.</param>
    /// <returns>The directory permission policy.</returns>
    public static DirectoryPermissionPolicy Create(WorkspacePaths paths, PermissionsDocument? document)
    {
        return new DirectoryPermissionPolicy(document, paths.RootPath);
    }

    /// <summary>
    /// Evaluates an operation against the most-specific matching rule.
    /// </summary>
    /// <param name="directoryPath">The absolute or workspace-relative directory to evaluate.</param>
    /// <param name="operation">The file-system operation being requested.</param>
    /// <returns>A denial, unconditional allowance, or human-approval requirement.</returns>
    public PermissionEvaluation EvaluateDirectory(string directoryPath, FileSystemOperation operation)
    {
        if (_document is null)
        {
            return PermissionEvaluation.RequiresApproval("", PermissionEvaluationDetails.MissingOrUnsupportedDocument);
        }

        var candidatePath = Path.GetFullPath(directoryPath);
        var approvedMatch = FindBestMatch(_document.Approved, candidatePath, operation);
        var deniedMatch = FindBestMatch(_document.Denied, candidatePath, operation);

        // The most-specific rule wins; a deny wins an equal-specificity tie so ambiguity fails closed.
        if (deniedMatch is not null && (approvedMatch is null || deniedMatch.Specificity >= approvedMatch.Specificity))
        {
            return PermissionEvaluation.Denied(deniedMatch.Entry.Path, PermissionEvaluationDetails.ExplicitDirectoryDeny);
        }

        if (approvedMatch?.Entry is ApprovedFileSystemPermission approvedEntry)
        {
            return approvedEntry.RequiresApproval ? PermissionEvaluation.RequiresApproval(approvedEntry.Path, PermissionEvaluationDetails.ApprovedDirectoryRequiresHumanApproval) : PermissionEvaluation.Allowed(approvedEntry.Path);
        }

        return PermissionEvaluation.RequiresApproval("", PermissionEvaluationDetails.NoMatchingDirectoryRule);
    }

    /// <summary>
    /// Determines whether reading the directory is unconditionally allowed.
    /// </summary>
    /// <param name="directoryPath">The directory path.</param>
    /// <returns><see langword="true"/> only when no human approval is required.</returns>
    public bool CanReadDirectory(string directoryPath) => EvaluateDirectory(directoryPath, FileSystemOperation.Read).Decision == PermissionDecision.Allow;

    /// <summary>
    /// Determines whether appending beneath the directory is unconditionally allowed.
    /// </summary>
    /// <param name="directoryPath">The directory path.</param>
    /// <returns><see langword="true"/> only when no human approval is required.</returns>
    public bool CanAppendDirectory(string directoryPath) => EvaluateDirectory(directoryPath, FileSystemOperation.Append).Decision == PermissionDecision.Allow;

    /// <summary>
    /// Determines whether modifying beneath the directory is unconditionally allowed.
    /// </summary>
    /// <param name="directoryPath">The directory path.</param>
    /// <returns><see langword="true"/> only when no human approval is required.</returns>
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

            if (!FileSystemPathComparer.IsWithinOrEqual(candidatePath, rulePath))
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

}

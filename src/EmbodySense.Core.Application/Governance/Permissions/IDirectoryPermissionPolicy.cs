using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Application.Governance.Permissions;

/// <summary>
/// Evaluates directory-scoped file-system authority from a versioned permission policy.
/// </summary>
public interface IDirectoryPermissionPolicy
{
    /// <summary>
    /// Gets a value indicating whether a compatible permission document was loaded.
    /// </summary>
    /// <value><see langword="true"/> when a compatible document is available; otherwise, <see langword="false"/>.</value>
    bool HasDocument { get; }

    /// <summary>
    /// Gets the approved rules, or an empty list when no compatible document exists.
    /// </summary>
    /// <value>The approved file system permissions.</value>
    IReadOnlyList<ApprovedFileSystemPermission> Approved { get; }

    /// <summary>
    /// Gets the denied rules, or an empty list when no compatible document exists.
    /// </summary>
    /// <value>The denied file system permissions.</value>
    IReadOnlyList<DeniedFileSystemPermission> Denied { get; }

    /// <summary>
    /// Evaluates an operation using most-specific-rule precedence and fail-closed defaults.
    /// </summary>
    /// <param name="directoryPath">The directory to evaluate.</param>
    /// <param name="operation">The requested file-system operation.</param>
    /// <returns>A denial, unconditional allowance, or human-approval requirement.</returns>
    PermissionEvaluation EvaluateDirectory(string directoryPath, FileSystemOperation operation);

    /// <summary>
    /// Determines whether reading the directory is unconditionally allowed.
    /// </summary>
    /// <param name="directoryPath">The directory path.</param>
    /// <returns><see langword="true"/> only when no human approval is required.</returns>
    bool CanReadDirectory(string directoryPath);

    /// <summary>
    /// Determines whether appending beneath the directory is unconditionally allowed.
    /// </summary>
    /// <param name="directoryPath">The directory path.</param>
    /// <returns><see langword="true"/> only when no human approval is required.</returns>
    bool CanAppendDirectory(string directoryPath);

    /// <summary>
    /// Determines whether modifying beneath the directory is unconditionally allowed.
    /// </summary>
    /// <param name="directoryPath">The directory path.</param>
    /// <returns><see langword="true"/> only when no human approval is required.</returns>
    bool CanModifyDirectory(string directoryPath);
}

using EmbodySense.Core.Common.LocalWorkspace;
using EmbodySense.Core.Common.LocalWorkspace.Models;

namespace EmbodySense.Core.Application.LocalWorkspace;

/// <summary>
/// Performs already-authorized operations against canonical workspace paths.
/// </summary>
public interface IWorkspaceToolExecutor
{
    /// <summary>
    /// Lists a canonical directory.
    /// </summary>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The bounded listing and audit metadata.</returns>
    Task<LocalWorkspaceResult> ListAsync(string resolvedPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a canonical file.
    /// </summary>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The bounded content and audit metadata.</returns>
    Task<LocalWorkspaceResult> ReadAsync(string resolvedPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches a canonical file or directory for a pattern.
    /// </summary>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The bounded matches and audit metadata.</returns>
    Task<LocalWorkspaceResult> SearchAsync(string resolvedPath, string? pattern, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends content to a canonical file, creating it when absent.
    /// </summary>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="content">The content.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The mutation summary and audit metadata.</returns>
    Task<LocalWorkspaceResult> AppendAsync(string resolvedPath, string? content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a canonical file's content, creating it when absent.
    /// </summary>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="content">The content.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The mutation summary and audit metadata.</returns>
    Task<LocalWorkspaceResult> WriteAsync(string resolvedPath, string? content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a canonical file or directory.
    /// </summary>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The mutation summary and audit metadata.</returns>
    Task<LocalWorkspaceResult> DeleteAsync(string resolvedPath, CancellationToken cancellationToken = default);
}

namespace EmbodySense.Core.Startup.Workspace;

/// <summary>
/// Defines the interface-layer boundary for applying the version-one EmbodySense workspace scaffold.
/// </summary>
public interface IWorkspaceInitializer
{
    /// <summary>
    /// Creates or refreshes the required workspace directories and seed documents.
    /// </summary>
    /// <param name="rootPath">The workspace root, normalized to an absolute path by the implementation.</param>
    /// <param name="cancellationToken">The token used to cancel seed writes and audit recording.</param>
    /// <returns>A task that completes after scaffolding and its success audit event have been written.</returns>
    /// <remarks>
    /// Initialization is not a multi-file transaction. Existing non-overwritable seed files are
    /// preserved, and cancellation or I/O failures may leave earlier scaffold changes in place.
    /// </remarks>
    Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default);
}

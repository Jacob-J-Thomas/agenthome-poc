using EmbodySense.Core.Common.Context;
using EmbodySense.Core.Common.Context.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Context;

/// <summary>
/// Loads workspace startup documents while retaining their authority kind and path provenance.
/// </summary>
public interface IWorkspaceContextStore
{
    /// <summary>
    /// Loads the available startup documents in deterministic injection order.
    /// </summary>
    /// <param name="paths">The resolved workspace paths containing the context documents.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The discovered documents, including their kind and display path.</returns>
    Task<IReadOnlyList<WorkspaceContextDocument>> LoadDocumentsAsync(WorkspacePaths paths, CancellationToken cancellationToken = default);
}

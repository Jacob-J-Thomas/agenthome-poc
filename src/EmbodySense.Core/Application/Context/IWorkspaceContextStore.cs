using EmbodySense.Core.Persistence.Workspace.Models;

namespace EmbodySense.Core.Application.Context;

public interface IWorkspaceContextStore
{
    Task<IReadOnlyList<WorkspaceContextDocument>> LoadDocumentsAsync(WorkspacePaths paths, CancellationToken cancellationToken = default);
}

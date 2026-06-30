using EmbodySense.Core.Common.Context;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Context;

public interface IWorkspaceContextStore
{
    Task<IReadOnlyList<WorkspaceContextDocument>> LoadDocumentsAsync(WorkspacePaths paths, CancellationToken cancellationToken = default);
}

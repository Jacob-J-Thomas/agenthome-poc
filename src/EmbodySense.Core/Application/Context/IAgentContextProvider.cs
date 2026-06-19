using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Persistence.Workspace.Models;

namespace EmbodySense.Core.Application.Context;

public interface IAgentContextProvider
{
    Task<IReadOnlyList<LlmMessage>> LoadAsync(WorkspacePaths paths, CancellationToken cancellationToken = default);
}

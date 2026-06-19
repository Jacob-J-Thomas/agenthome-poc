using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Core.Context;

public interface IAgentContextProvider
{
    Task<IReadOnlyList<LlmMessage>> LoadAsync(WorkspacePaths paths, CancellationToken cancellationToken = default);
}

using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Context;

public interface IAgentContextProvider
{
    Task<IReadOnlyList<LlmMessage>> LoadAsync(WorkspacePaths paths, CancellationToken cancellationToken = default);
}

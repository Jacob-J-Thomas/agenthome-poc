using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Context;

/// <summary>
/// Provides the ordered startup messages injected into an agent conversation.
/// </summary>
public interface IAgentContextProvider
{
    /// <summary>
    /// Loads role instructions, durable identity, and lower-authority workspace state.
    /// </summary>
    /// <param name="paths">The resolved paths for the workspace whose context is being loaded.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The startup messages in injection order, or an empty list when no context is available.</returns>
    Task<IReadOnlyList<LlmMessage>> LoadAsync(WorkspacePaths paths, CancellationToken cancellationToken = default);
}

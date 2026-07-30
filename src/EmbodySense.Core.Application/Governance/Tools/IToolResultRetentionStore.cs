using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>
/// Durably stores a full tool response and returns integrity-verifiable evidence for the retained artifact.
/// </summary>
public interface IToolResultRetentionStore
{
    /// <summary>
    /// Retains the model-facing result under the active loop's evidence policy.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <param name="loopDefinition">The loop definition.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A manifest reference, content hash, size, and quota evidence for the retained response.</returns>
    Task<ToolResultRetentionReference> RetainAsync(ToolResult result, LoopDefinition loopDefinition, CancellationToken cancellationToken = default);
}

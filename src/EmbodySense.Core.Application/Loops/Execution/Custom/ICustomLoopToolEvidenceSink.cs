using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Records governed tool outcomes against an exact run-attempt coordinate.
/// </summary>
public interface ICustomLoopToolEvidenceSink
{
    /// <summary>
    /// Appends one tool outcome evidence record to the active attempt.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="iteration">The iteration.</param>
    /// <param name="stepId">The step ID.</param>
    /// <param name="attempt">The attempt.</param>
    /// <param name="evidence">The evidence.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task RecordAsync(string runId, int iteration, string stepId, int attempt, CustomLoopToolTraceEvidence evidence, CancellationToken cancellationToken = default);
}

using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Continues execution after a durable authenticated resume transition.
/// </summary>
public interface ICustomLoopResumeExecutor
{
    /// <summary>
    /// Continues the exact running lifecycle version named by the resume request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The continued execution result or a fail-closed ownership/state result.</returns>
    Task<CustomLoopOrderedRunResult> ResumeAsync(CustomLoopResumeExecutionRequest request, CancellationToken cancellationToken = default);
}

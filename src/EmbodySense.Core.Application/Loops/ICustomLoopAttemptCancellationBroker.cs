using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Coordinates in-process cancellation of the provider attempt currently active for a run.
/// </summary>
public interface ICustomLoopAttemptCancellationBroker
{
    /// <summary>
    /// Registers the active attempt and returns a lease that removes the registration when disposed.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellation">The cancellation.</param>
    /// <param name="competingCancellationToken">The competing cancellation token.</param>
    /// <returns>The custom loop attempt cancellation registration.</returns>
    ICustomLoopAttemptCancellationRegistration RegisterActiveAttempt(string runId, CancellationTokenSource cancellation, CancellationToken competingCancellationToken = default);

    /// <summary>
    /// Requests cancellation of the registered attempt for an idempotent control operation.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>Whether cancellation was signaled, already requested, or no attempt was active.</returns>
    Task<CustomLoopAttemptCancellationResult> RequestCancellationAsync(string runId, string operationId, CancellationToken cancellationToken = default);
}

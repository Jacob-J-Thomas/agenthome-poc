using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Coordinates local run ownership and cancellation of the currently active provider attempt.
/// </summary>
public interface ICustomLoopExecutionCancellationSignal
{
    /// <summary>
    /// Attempts to register active run.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <returns>An ownership lease, or <see langword="null"/> when the run is already locally owned.</returns>
    IDisposable? TryRegisterActiveRun(string runId);

    /// <summary>
    /// Signals cancellation to an active attempt owned by the current runtime.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    void CancelActiveAttempt(string runId);

    /// <summary>
    /// Requests idempotent cancellation of the active provider attempt.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>Whether the signal was delivered, no attempt was active, or its owner was unavailable.</returns>
    Task<CustomLoopAttemptCancellationResult> RequestActiveAttemptCancellationAsync(string runId, string operationId, CancellationToken cancellationToken = default);
}

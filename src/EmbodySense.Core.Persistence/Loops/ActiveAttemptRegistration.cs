using EmbodySense.Core.Application.Loops;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Owns generation-bound completion of one cancellation-host attempt registration.
/// </summary>
/// <param name="host">The host.</param>
/// <param name="runId">The run ID.</param>
/// <param name="attempt">The attempt.</param>
internal sealed class ActiveAttemptRegistration(CustomLoopAttemptCancellationHost host, string runId, ActiveAttempt attempt) : ICustomLoopAttemptCancellationRegistration
{
    private int _completed;

    /// <summary>
    /// Attempts to confirm provider interruption.
    /// </summary>
    /// <param name="observedCancellationToken">The observed cancellation token.</param>
    /// <returns><see langword="true"/> when confirm provider interruption; otherwise, <see langword="false"/>.</returns>
    public bool TryConfirmProviderInterruption(CancellationToken observedCancellationToken)
    {
        if (!attempt.CanConfirmProviderInterruption(observedCancellationToken) || Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return false;
        }

        host.CompleteAttempt(runId, attempt.Generation, interrupted: true);
        return true;
    }

    /// <summary>
    /// Completes the registration without claiming confirmed provider interruption.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            host.CompleteAttempt(runId, attempt.Generation, interrupted: false);
        }
    }
}

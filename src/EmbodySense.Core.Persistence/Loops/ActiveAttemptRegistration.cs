using EmbodySense.Core.Application.Loops;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class ActiveAttemptRegistration(CustomLoopAttemptCancellationHost host, string runId, ActiveAttempt attempt) : ICustomLoopAttemptCancellationRegistration
{
    private int _completed;

    public bool TryConfirmProviderInterruption(CancellationToken observedCancellationToken)
    {
        if (!attempt.CanConfirmProviderInterruption(observedCancellationToken) || Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return false;
        }

        host.CompleteAttempt(runId, attempt.Generation, interrupted: true);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            host.CompleteAttempt(runId, attempt.Generation, interrupted: false);
        }
    }
}

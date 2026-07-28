namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopAttemptCancellationRegistration : IDisposable
{
    bool TryConfirmProviderInterruption(CancellationToken observedCancellationToken);
}

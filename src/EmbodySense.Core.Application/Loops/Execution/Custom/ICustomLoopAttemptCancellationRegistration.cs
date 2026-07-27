namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopAttemptCancellationRegistration : IDisposable
{
    bool TryConfirmProviderInterruption(CancellationToken observedCancellationToken);
}

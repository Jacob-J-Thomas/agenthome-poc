namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopAttemptCancellationRegistration : IDisposable
{
    void ConfirmProviderInterruption();
}

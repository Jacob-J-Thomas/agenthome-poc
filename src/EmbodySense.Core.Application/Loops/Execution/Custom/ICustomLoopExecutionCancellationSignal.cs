using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopExecutionCancellationSignal
{
    IDisposable? TryRegisterActiveRun(string runId);

    void CancelActiveAttempt(string runId);
}

using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopExecutionCancellationSignal
{
    IDisposable? TryRegisterActiveRun(string runId);

    void CancelActiveAttempt(string runId);

    Task<CustomLoopAttemptCancellationResult> RequestActiveAttemptCancellationAsync(string runId, string operationId, CancellationToken cancellationToken = default);
}

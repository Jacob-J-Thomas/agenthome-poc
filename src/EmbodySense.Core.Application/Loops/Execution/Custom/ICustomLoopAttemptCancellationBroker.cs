using EmbodySense.Core.Application.Loops.Execution.Custom.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopAttemptCancellationBroker
{
    ICustomLoopAttemptCancellationRegistration RegisterActiveAttempt(string runId, CancellationTokenSource cancellation);

    Task<CustomLoopAttemptCancellationResult> RequestCancellationAsync(string runId, string operationId, CancellationToken cancellationToken = default);
}

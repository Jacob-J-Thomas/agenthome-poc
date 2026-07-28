using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopAttemptCancellationBroker
{
    ICustomLoopAttemptCancellationRegistration RegisterActiveAttempt(string runId, CancellationTokenSource cancellation, CancellationToken competingCancellationToken = default);

    Task<CustomLoopAttemptCancellationResult> RequestCancellationAsync(string runId, string operationId, CancellationToken cancellationToken = default);
}

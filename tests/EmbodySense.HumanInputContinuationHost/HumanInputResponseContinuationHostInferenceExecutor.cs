using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostInferenceExecutor : ICustomLoopInferenceAttemptExecutor
{
    public Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null)
        => throw new InvalidOperationException("The bounded process fixture advances only the downstream pure node and must not issue inference.");
}

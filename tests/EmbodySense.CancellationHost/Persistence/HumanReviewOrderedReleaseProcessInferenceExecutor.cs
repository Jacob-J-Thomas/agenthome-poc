using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessInferenceExecutor : ICustomLoopInferenceAttemptExecutor
{
    public Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null)
        => throw new InvalidOperationException("The process-observable Human Review graph contains no inference node.");
}

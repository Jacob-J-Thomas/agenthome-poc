using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessRuntime : IGovernedLoopSequentialOrderedRuntime
{
    public Task<CustomLoopOrderedRunResult> RunAsync(GovernedLoopSequentialOrderedRunRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.InvalidState, null, "The process verifier does not own initial execution."));

    public Task<CustomLoopOrderedRunResult> ResumeAsync(GovernedLoopSequentialOrderedResumeRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Paused, null, "The persisted release is replayable without dispatching a dependent node in this verifier."));
}

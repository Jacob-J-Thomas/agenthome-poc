using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Continuations;

internal sealed class CancellationBarrierRecordingOrderedRuntime : IGovernedLoopSequentialOrderedRuntime
{
    internal int ResumeHumanInputCount { get; private set; }

    internal int ResumeHumanInputFailureCount { get; private set; }

    public Task<CustomLoopOrderedRunResult> RunAsync(GovernedLoopSequentialOrderedRunRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CustomLoopOrderedRunResult> ResumeAsync(GovernedLoopSequentialOrderedResumeRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CustomLoopOrderedRunResult> ResumeHumanInputAsync(GovernedLoopSequentialOrderedHumanInputResumeRequest request, CancellationToken cancellationToken = default)
    {
        ResumeHumanInputCount++;
        return Task.FromResult(new CustomLoopOrderedRunResult(
            CustomLoopOrderedRunStatus.InvalidState,
            null,
            "The queued-continuation cancellation test must not dispatch ordered Human Input re-entry."));
    }

    public Task<CustomLoopOrderedRunResult> ResumeHumanInputFailureAsync(GovernedLoopSequentialOrderedHumanInputFailureResumeRequest request, CancellationToken cancellationToken = default)
    {
        ResumeHumanInputFailureCount++;
        return Task.FromResult(new CustomLoopOrderedRunResult(
            CustomLoopOrderedRunStatus.InvalidState,
            null,
            "The queued-continuation cancellation test must not dispatch ordered Human Input failure re-entry."));
    }
}

using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class HumanReviewOrderedReleaseTestRuntime : IGovernedLoopSequentialOrderedRuntime
{
    private readonly Func<GovernedLoopSequentialOrderedResumeRequest, CancellationToken, Task>? _afterResume;
    private readonly object _handoffGate = new();
    private Task? _handoff;

    public HumanReviewOrderedReleaseTestRuntime(Func<GovernedLoopSequentialOrderedResumeRequest, CancellationToken, Task>? afterResume = null)
    {
        _afterResume = afterResume;
    }

    public int ResumeCount { get; private set; }

    public Task<CustomLoopOrderedRunResult> RunAsync(GovernedLoopSequentialOrderedRunRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.InvalidState, null, "This test double does not own initial execution."));

    public async Task<CustomLoopOrderedRunResult> ResumeAsync(GovernedLoopSequentialOrderedResumeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task handoff;
        lock (_handoffGate)
        {
            _handoff ??= ConfirmAsync(request, cancellationToken);
            handoff = _handoff;
        }

        await handoff.ConfigureAwait(false);
        return new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Paused, null, "The exact released Human Review frontier re-entered the existing ordered runtime.");
    }

    private Task ConfirmAsync(GovernedLoopSequentialOrderedResumeRequest request, CancellationToken cancellationToken)
    {
        ResumeCount++;
        return _afterResume?.Invoke(request, cancellationToken) ?? Task.CompletedTask;
    }
}

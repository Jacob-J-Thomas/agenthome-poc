using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostOrderedRuntime : IGovernedLoopSequentialOrderedRuntime
{
    private readonly IGovernedLoopSequentialOrderedRuntime _inner;

    internal HumanInputResponseContinuationHostOrderedRuntime(IGovernedLoopSequentialOrderedRuntime inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    internal CustomLoopOrderedRunResult? LastResult { get; private set; }

    public Task<CustomLoopOrderedRunResult> RunAsync(GovernedLoopSequentialOrderedRunRequest request, CancellationToken cancellationToken = default)
        => CaptureAsync(_inner.RunAsync(request, cancellationToken));

    public Task<CustomLoopOrderedRunResult> ResumeAsync(GovernedLoopSequentialOrderedResumeRequest request, CancellationToken cancellationToken = default)
        => CaptureAsync(_inner.ResumeAsync(request, cancellationToken));

    public Task<CustomLoopOrderedRunResult> ResumeWaitAsync(GovernedLoopSequentialOrderedWaitResumeRequest request, CancellationToken cancellationToken = default)
        => CaptureAsync(_inner.ResumeWaitAsync(request, cancellationToken));

    public Task<CustomLoopOrderedRunResult> ResumeHumanInputAsync(GovernedLoopSequentialOrderedHumanInputResumeRequest request, CancellationToken cancellationToken = default)
        => CaptureAsync(_inner.ResumeHumanInputAsync(request, cancellationToken));

    public Task<CustomLoopOrderedRunResult> ResumeHumanInputFailureAsync(GovernedLoopSequentialOrderedHumanInputFailureResumeRequest request, CancellationToken cancellationToken = default)
        => CaptureAsync(_inner.ResumeHumanInputFailureAsync(request, cancellationToken));

    public Task<CustomLoopOrderedRunResult> ResumeRetryAsync(GovernedLoopSequentialOrderedRetryResumeRequest request, CancellationToken cancellationToken = default)
        => CaptureAsync(_inner.ResumeRetryAsync(request, cancellationToken));

    private async Task<CustomLoopOrderedRunResult> CaptureAsync(Task<CustomLoopOrderedRunResult> work)
    {
        var result = await work.ConfigureAwait(false);
        LastResult = result;
        return result;
    }
}

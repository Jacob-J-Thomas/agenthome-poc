using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Executes or resumes one admitted canonical sequential graph through the durable ordered runtime.</summary>
public interface IGovernedLoopSequentialOrderedRuntime
{
    /// <summary>Starts ordered execution from the exact immutable admission hand-off.</summary>
    Task<CustomLoopOrderedRunResult> RunAsync(
        GovernedLoopSequentialOrderedRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Continues ordered execution from an exact durable resume transition.</summary>
    Task<CustomLoopOrderedRunResult> ResumeAsync(
        GovernedLoopSequentialOrderedResumeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Re-enters ordered execution from an exact durable Wait continuation.</summary>
    Task<CustomLoopOrderedRunResult> ResumeWaitAsync(
        GovernedLoopSequentialOrderedWaitResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CustomLoopOrderedRunResult(
            CustomLoopOrderedRunStatus.InvalidState,
            null,
            "This ordered runtime does not support canonical Wait re-entry."));
    }

    /// <summary>Re-enters ordered execution from an exact durable retry dispatch or routed exhaustion.</summary>
    Task<CustomLoopOrderedRunResult> ResumeRetryAsync(
        GovernedLoopSequentialOrderedRetryResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CustomLoopOrderedRunResult(
            CustomLoopOrderedRunStatus.InvalidState,
            null,
            "This ordered runtime does not support canonical retry re-entry."));
    }
}

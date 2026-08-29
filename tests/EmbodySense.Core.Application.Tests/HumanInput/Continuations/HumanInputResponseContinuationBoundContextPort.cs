using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.HumanInput.Continuations;

internal sealed class HumanInputResponseContinuationBoundContextPort : IGovernedLoopWaitOrderedResumePort
{
    internal HumanInputResponseContinuationBoundContextPort(GovernedLoopWaitOrderedContext context)
    {
        Context = context;
    }

    internal GovernedLoopWaitOrderedContext? Context { get; set; }

    internal Exception? ResolveException { get; set; }

    internal int? ResolveExceptionOnCall { get; set; }

    internal int? NullOnResolveCall { get; set; }

    internal Action<int>? BeforeResolve { get; set; }

    internal int ResolveCount { get; private set; }

    public Task<GovernedLoopWaitOrderedContext?> ResolveAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
    {
        ResolveCount++;
        BeforeResolve?.Invoke(ResolveCount);
        if (ResolveException is not null && (ResolveExceptionOnCall is null || ResolveExceptionOnCall == ResolveCount)
            || ResolveException is null && ResolveExceptionOnCall == ResolveCount)
        {
            throw ResolveException ?? new IOException("simulated ordered-context outage");
        }

        return Task.FromResult(NullOnResolveCall == ResolveCount ? null : Context);
    }

    public Task<CustomLoopOrderedRunResult> ResumeAsync(GovernedLoopWaitOrderedResumeRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.InvalidState, null, "Human Input uses its dedicated ordered re-entry."));
}

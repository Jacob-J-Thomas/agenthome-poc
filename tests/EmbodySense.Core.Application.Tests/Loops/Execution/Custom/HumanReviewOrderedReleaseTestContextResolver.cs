using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class HumanReviewOrderedReleaseTestContextResolver(GovernedLoopWaitOrderedContext context) : IGovernedLoopWaitOrderedResumePort
{
    public Task<GovernedLoopWaitOrderedContext?> ResolveAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<GovernedLoopWaitOrderedContext?>(context);
    }

    public Task<CustomLoopOrderedRunResult> ResumeAsync(GovernedLoopWaitOrderedResumeRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.InvalidState, null, "This test double does not own Wait re-entry."));
}

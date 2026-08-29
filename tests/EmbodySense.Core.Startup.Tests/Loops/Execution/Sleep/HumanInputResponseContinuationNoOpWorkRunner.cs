using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanInputResponseContinuationNoOpWorkRunner : IGovernedLoopLocalWorkRunner
{
    public Task<GovernedLoopLocalWorkResult?> RunOnceAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<GovernedLoopLocalWorkResult?>(new GovernedLoopLocalWorkResult(
            GovernedLoopLocalWorkResultStatus.Empty,
            "test-inner-no-work"));
    }
}

using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryRecordingWorkRunner : IGovernedLoopLocalWorkRunner
{
    public int Calls { get; private set; }

    public Task<GovernedLoopLocalWorkResult?> RunOnceAsync(GovernedLoopLocalWorkFamily family, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult<GovernedLoopLocalWorkResult?>(new(GovernedLoopLocalWorkResultStatus.Completed, "delegated"));
    }
}

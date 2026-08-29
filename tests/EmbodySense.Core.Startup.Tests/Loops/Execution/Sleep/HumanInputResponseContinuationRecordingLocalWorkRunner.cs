using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanInputResponseContinuationRecordingLocalWorkRunner : IGovernedLoopLocalWorkRunner
{
    internal List<GovernedLoopLocalWorkFamily> Families { get; } = [];

    internal GovernedLoopLocalWorkResult Result { get; } = new(GovernedLoopLocalWorkResultStatus.Completed, "inner");

    public Task<GovernedLoopLocalWorkResult?> RunOnceAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Families.Add(family);
        return Task.FromResult<GovernedLoopLocalWorkResult?>(Result);
    }
}

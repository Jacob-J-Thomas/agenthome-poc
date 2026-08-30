using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

internal sealed class StubGovernedLoopSleepCurrentPosturePort : IGovernedLoopSleepCurrentPosturePort
{
    internal GovernedLoopSleepCurrentPostureReadResult? Result { get; set; }

    internal Exception? Exception { get; set; }

    internal int ReadCount { get; private set; }

    internal GovernedLoopExecutionBinding? LastBinding { get; private set; }

    internal Func<int, Task>? BeforeReadAsync { get; set; }

    public async Task<GovernedLoopSleepCurrentPostureReadResult?> ReadAsync(
        GovernedLoopExecutionBinding binding,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        LastBinding = binding;
        if (BeforeReadAsync is not null)
        {
            await BeforeReadAsync(ReadCount).ConfigureAwait(false);
        }
        if (Exception is not null)
        {
            throw Exception;
        }

        return Result;
    }
}

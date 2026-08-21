using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class ScriptedBackgroundWorkSource : IGovernedLoopBackgroundWorkSource
{
    internal Func<GovernedLoopBackgroundWorkFamily, DateTimeOffset, int, CancellationToken, Task<GovernedLoopBackgroundWorkReadResult?>> Handler { get; set; }
        = static (_, _, _, _) => Task.FromResult<GovernedLoopBackgroundWorkReadResult?>(
            GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
                GovernedLoopBackgroundWorkReadStatus.Empty,
                [],
                [],
                []));

    internal int Calls { get; private set; }

    internal int LastPerFamilyMax { get; private set; }

    internal DateTimeOffset LastObservedAtUtc { get; private set; }

    public Task<GovernedLoopBackgroundWorkReadResult?> ReadAsync(
        GovernedLoopBackgroundWorkFamily family,
        DateTimeOffset observedAtUtc,
        int perFamilyMax,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastObservedAtUtc = observedAtUtc;
        LastPerFamilyMax = perFamilyMax;
        return Handler(family, observedAtUtc, perFamilyMax, cancellationToken);
    }
}

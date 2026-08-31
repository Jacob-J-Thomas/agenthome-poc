using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class StaticCoordinatorRepairDependencyPort : IGovernedLoopCoordinatorRepairDependencyPort
{
    internal int ReadCalls { get; private set; }

    internal bool Ready { get; set; } = true;

    public Task<GovernedLoopCoordinatorRepairReadiness?> ReadAsync(
        string workspaceId,
        string coordinatorId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCalls++;
        return Task.FromResult<GovernedLoopCoordinatorRepairReadiness?>(GovernedLoopSleepContractHash.Apply(
            new GovernedLoopCoordinatorRepairReadiness(
                1,
                workspaceId,
                coordinatorId,
                Ready,
                Ready,
                Ready,
                Ready,
                new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                string.Empty)));
    }
}

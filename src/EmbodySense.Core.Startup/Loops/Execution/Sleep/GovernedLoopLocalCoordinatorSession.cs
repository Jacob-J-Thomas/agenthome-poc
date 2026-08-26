using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

internal sealed class GovernedLoopLocalCoordinatorSession : IDisposable
{
    internal GovernedLoopLocalCoordinatorSession(
        GovernedLoopCoordinatorSnapshot snapshot,
        int workFamilyCount)
    {
        LocalOwnership = snapshot.Ownership;
        Snapshot = snapshot;
        BackpressureRecorded = new bool[workFamilyCount + 1];
    }

    internal CancellationTokenSource AdmissionStop { get; } = new();

    internal bool[] BackpressureRecorded { get; }

    internal Task<GovernedLoopLocalCoordinatorSessionOutcome> Completion { get; set; } = Task.FromResult(
        new GovernedLoopLocalCoordinatorSessionOutcome(GovernedLoopLocalCoordinatorStopStatus.Failed, null!));

    internal long CycleNumber { get; set; }

    internal CancellationTokenSource HeartbeatStop { get; } = new();

    internal GovernedLoopCoordinatorOwnership LocalOwnership { get; }

    internal int OwnershipLossParked;

    internal GovernedLoopCoordinatorSnapshot Snapshot { get; set; }

    internal int StopRequested;

    public void Dispose()
    {
        AdmissionStop.Dispose();
        HeartbeatStop.Dispose();
    }
}

using EmbodySense.Core.Startup.Loops.Execution.Sleep;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class ThrowingCoordinatorBoundaryObserver : IGovernedLoopLocalCoordinatorBoundaryObserver
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    public void OnHeartbeatDue()
    {
        Interlocked.Increment(ref _calls);
        throw new IOException("hostile diagnostic observer");
    }
}

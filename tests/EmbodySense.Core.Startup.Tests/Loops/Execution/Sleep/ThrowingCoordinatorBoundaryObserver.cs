using EmbodySense.Core.Startup.Loops.Execution.Sleep;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class ThrowingCoordinatorBoundaryObserver : IGovernedLoopLocalCoordinatorBoundaryObserver
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    public void OnHeartbeatDue() => Throw();

    public void OnOwnershipLost() => Throw();

    public void OnForeignSessionMutationSuppressed() => Throw();

    private void Throw()
    {
        Interlocked.Increment(ref _calls);
        throw new IOException("hostile diagnostic observer");
    }
}

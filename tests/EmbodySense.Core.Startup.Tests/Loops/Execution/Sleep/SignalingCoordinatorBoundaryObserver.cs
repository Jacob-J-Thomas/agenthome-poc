using EmbodySense.Core.Startup.Loops.Execution.Sleep;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class SignalingCoordinatorBoundaryObserver : IGovernedLoopLocalCoordinatorBoundaryObserver
{
    private readonly TaskCompletionSource _heartbeatDue = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task HeartbeatDue => _heartbeatDue.Task;

    public void OnHeartbeatDue() => _heartbeatDue.TrySetResult();
}

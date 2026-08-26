using EmbodySense.Core.Startup.Loops.Execution.Sleep;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class SignalingCoordinatorBoundaryObserver : IGovernedLoopLocalCoordinatorBoundaryObserver
{
    private readonly TaskCompletionSource _heartbeatDue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _foreignSessionMutationSuppressed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ownershipLost = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task HeartbeatDue => _heartbeatDue.Task;

    internal Task ForeignSessionMutationSuppressed => _foreignSessionMutationSuppressed.Task;

    internal Task OwnershipLost => _ownershipLost.Task;

    public void OnHeartbeatDue() => _heartbeatDue.TrySetResult();

    public void OnOwnershipLost() => _ownershipLost.TrySetResult();

    public void OnForeignSessionMutationSuppressed() => _foreignSessionMutationSuppressed.TrySetResult();
}

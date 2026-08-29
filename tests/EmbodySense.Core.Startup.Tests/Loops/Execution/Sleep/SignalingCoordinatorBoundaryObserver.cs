using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class SignalingCoordinatorBoundaryObserver : IGovernedLoopLocalCoordinatorBoundaryObserver
{
    private readonly TaskCompletionSource _heartbeatDue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _foreignSessionMutationSuppressed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _humanInputWorkAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ownershipLost = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task HeartbeatDue => _heartbeatDue.Task;

    internal Task ForeignSessionMutationSuppressed => _foreignSessionMutationSuppressed.Task;

    internal Task OwnershipLost => _ownershipLost.Task;

    internal Task HumanInputWorkAttempted => _humanInputWorkAttempted.Task;

    internal bool ThrowOnOwnershipLost { get; set; }

    internal bool ThrowOnForeignSessionMutationSuppressed { get; set; }

    public void OnHeartbeatDue() => _heartbeatDue.TrySetResult();

    public void OnWorkFamilyAttempted(GovernedLoopLocalWorkFamily family)
    {
        if (family == GovernedLoopLocalWorkFamily.HumanInput)
        {
            _humanInputWorkAttempted.TrySetResult();
        }
    }

    public void OnOwnershipLost()
    {
        _ownershipLost.TrySetResult();
        if (ThrowOnOwnershipLost)
        {
            throw new IOException("hostile ownership observer");
        }
    }

    public void OnForeignSessionMutationSuppressed()
    {
        _foreignSessionMutationSuppressed.TrySetResult();
        if (ThrowOnForeignSessionMutationSuppressed)
        {
            throw new IOException("hostile foreign-session observer");
        }
    }
}

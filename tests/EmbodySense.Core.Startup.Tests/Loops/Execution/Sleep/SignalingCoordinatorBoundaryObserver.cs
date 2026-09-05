using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class SignalingCoordinatorBoundaryObserver : IGovernedLoopLocalCoordinatorBoundaryObserver
{
    private readonly TaskCompletionSource _heartbeatDue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _foreignSessionMutationSuppressed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _humanInputWorkAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ownershipLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _heartbeatRelease;
    private TaskCompletionSource? _heldHeartbeatDue;
    private TaskCompletionSource? _heldHumanInputWorkAttempted;
    private TaskCompletionSource? _humanInputWorkRelease;

    internal Task HeartbeatDue => _heartbeatDue.Task;

    internal Task HeldHeartbeatDue
        => Volatile.Read(ref _heldHeartbeatDue)?.Task
            ?? throw new InvalidOperationException("Heartbeat is not held.");

    internal Task ForeignSessionMutationSuppressed => _foreignSessionMutationSuppressed.Task;

    internal Task OwnershipLost => _ownershipLost.Task;

    internal Task HumanInputWorkAttempted => _humanInputWorkAttempted.Task;

    internal Task HeldHumanInputWorkAttempted
        => Volatile.Read(ref _heldHumanInputWorkAttempted)?.Task
            ?? throw new InvalidOperationException("Human Input work is not held.");

    internal bool ThrowOnOwnershipLost { get; set; }

    internal bool ThrowOnForeignSessionMutationSuppressed { get; set; }

    internal void HoldHeartbeat()
    {
        Volatile.Write(ref _heldHeartbeatDue, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Volatile.Write(ref _heartbeatRelease, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    internal void ReleaseHeartbeat()
        => Interlocked.Exchange(ref _heartbeatRelease, null)?.TrySetResult();

    internal void HoldHumanInputWork()
    {
        Volatile.Write(ref _heldHumanInputWorkAttempted, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Volatile.Write(ref _humanInputWorkRelease, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    internal void ReleaseHumanInputWork()
        => Interlocked.Exchange(ref _humanInputWorkRelease, null)?.TrySetResult();

    public void OnHeartbeatDue()
    {
        _heartbeatDue.TrySetResult();
        var release = Volatile.Read(ref _heartbeatRelease);
        if (release is not null)
        {
            Volatile.Read(ref _heldHeartbeatDue)?.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }
    }

    public void OnWorkFamilyAttempted(GovernedLoopLocalWorkFamily family)
    {
        if (family == GovernedLoopLocalWorkFamily.HumanInput)
        {
            _humanInputWorkAttempted.TrySetResult();
            var release = Volatile.Read(ref _humanInputWorkRelease);
            if (release is not null)
            {
                Volatile.Read(ref _heldHumanInputWorkAttempted)?.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
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

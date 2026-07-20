using EmbodySense.Core.Application.Loops;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class TestExecutionGate : ICustomLoopWorkspaceExecutionGate
{
    public TestExecutionGate(CustomLoopExecutionLeaseStatus status = CustomLoopExecutionLeaseStatus.Acquired)
    {
        Status = status;
    }

    public CustomLoopExecutionLeaseStatus Status { get; set; }

    public bool IsWorkspaceHostAvailable => Status != CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable;

    public int AcquisitionCount { get; private set; }

    public int ReleasedLeaseCount { get; private set; }

    public CustomLoopExecutionLeaseResult TryAcquire(string operationId, string requestHash)
    {
        AcquisitionCount++;
        var lease = Status == CustomLoopExecutionLeaseStatus.Acquired ? new Lease(operationId, () => ReleasedLeaseCount++) : null;
        return new CustomLoopExecutionLeaseResult(Status, lease, "Test execution ownership outcome.");
    }

    public CustomLoopExecutionLeaseResult TryReserveWorkspaceBusyOutcome(string operationId, string requestHash)
    {
        throw new NotSupportedException("Lifecycle service tests never persist invocation workspace-busy receipts.");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private sealed class Lease(string operationId, Action release) : ICustomLoopExecutionLease
    {
        public string OperationId { get; } = operationId;

        public void Dispose()
        {
            release();
        }
    }
}

using EmbodySense.Core.Application.Loops;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class TestExecutionGate : ICustomLoopWorkspaceExecutionGate
{
    private readonly CustomLoopExecutionLeaseStatus _status;

    public TestExecutionGate(CustomLoopExecutionLeaseStatus status = CustomLoopExecutionLeaseStatus.Acquired)
    {
        _status = status;
    }

    public int AcquisitionCount { get; private set; }

    public int ReleasedLeaseCount { get; private set; }

    public CustomLoopExecutionLeaseResult TryAcquire(string operationId, string requestHash)
    {
        AcquisitionCount++;
        var lease = _status == CustomLoopExecutionLeaseStatus.Acquired ? new Lease(operationId, () => ReleasedLeaseCount++) : null;
        return new CustomLoopExecutionLeaseResult(_status, lease, "Test execution ownership outcome.");
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

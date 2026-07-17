namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopExecutionLeaseStatus
{
    Unknown = 0,
    Acquired = 1,
    WorkspaceBusy = 2,
    OperationInProgress = 3,
    OperationConflict = 4
}

public sealed record CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus Status, ICustomLoopExecutionLease? Lease, string Detail);

public interface ICustomLoopExecutionLease : IDisposable
{
    string OperationId { get; }
}

public interface ICustomLoopWorkspaceExecutionGate : IAsyncDisposable
{
    CustomLoopExecutionLeaseResult TryAcquire(string operationId, string requestHash);
}

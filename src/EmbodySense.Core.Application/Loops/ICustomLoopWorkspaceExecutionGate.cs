using EmbodySense.Core.Application.Loops.Models;
namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopWorkspaceExecutionGate : IAsyncDisposable
{
    bool IsWorkspaceHostAvailable { get; }

    CustomLoopExecutionLeaseResult TryAcquire(string operationId, string requestHash);

    CustomLoopExecutionLeaseResult TryReserveWorkspaceBusyOutcome(string operationId, string requestHash);

    void RelinquishWorkspaceHost();
}

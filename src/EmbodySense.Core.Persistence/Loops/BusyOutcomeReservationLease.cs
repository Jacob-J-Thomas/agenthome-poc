using EmbodySense.Core.Application.Loops;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class BusyOutcomeReservationLease : ICustomLoopExecutionLease
{
    private readonly string _workspaceKey;
    private readonly WorkspaceHost _host;
    private readonly long _generation;
    private int _disposed;

    public BusyOutcomeReservationLease(string workspaceKey, WorkspaceHost host, string operationId, long generation)
    {
        _workspaceKey = workspaceKey;
        _host = host;
        OperationId = operationId;
        _generation = generation;
    }

    public string OperationId { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CustomLoopWorkspaceExecutionGate.ReleaseBusyOutcomeReservation(_workspaceKey, _host, OperationId, _generation);
        }
    }
}

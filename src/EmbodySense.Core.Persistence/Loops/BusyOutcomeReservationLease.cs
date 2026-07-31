using EmbodySense.Core.Application.Loops;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Releases one generation-bound in-memory workspace-busy outcome reservation on disposal.
/// </summary>
internal sealed class BusyOutcomeReservationLease : ICustomLoopExecutionLease
{
    private readonly string _workspaceKey;
    private readonly WorkspaceHost _host;
    private readonly long _generation;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BusyOutcomeReservationLease"/> type.
    /// </summary>
    /// <param name="workspaceKey">The workspace key.</param>
    /// <param name="host">The host.</param>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="generation">The generation.</param>
    public BusyOutcomeReservationLease(string workspaceKey, WorkspaceHost host, string operationId, long generation)
    {
        _workspaceKey = workspaceKey;
        _host = host;
        OperationId = operationId;
        _generation = generation;
    }

    /// <summary>
    /// Gets the operation ID.
    /// </summary>
    /// <value>The operation ID.</value>
    public string OperationId { get; }

    /// <summary>
    /// Idempotently releases the exact busy-outcome reservation generation owned by this lease.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CustomLoopWorkspaceExecutionGate.ReleaseBusyOutcomeReservation(_workspaceKey, _host, OperationId, _generation);
        }
    }
}

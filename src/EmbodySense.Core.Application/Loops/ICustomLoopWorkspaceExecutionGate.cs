using EmbodySense.Core.Application.Loops.Models;
namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Serializes custom-loop execution against the single workspace-host ownership boundary.
/// </summary>
public interface ICustomLoopWorkspaceExecutionGate : IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether the workspace host can currently execute a custom loop.
    /// </summary>
    /// <value><see langword="true"/> when the value is workspace host available; otherwise, <see langword="false"/>.</value>
    bool IsWorkspaceHostAvailable { get; }

    /// <summary>
    /// Attempts to acquire the execution lease for an idempotent invocation request.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="requestHash">The request hash.</param>
    /// <returns>The custom loop execution lease result.</returns>
    CustomLoopExecutionLeaseResult TryAcquire(string operationId, string requestHash);

    /// <summary>
    /// Reserves an in-memory busy-outcome lease while this process owns the host and another operation owns execution.
    /// </summary>
    /// <remarks>
    /// A missing workspace host produces <see cref="CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable"/>
    /// without a reservation. The caller establishes durability later through the invocation-operation store while
    /// holding the returned reservation lease.
    /// </remarks>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="requestHash">The request hash.</param>
    /// <returns>The custom loop execution lease result.</returns>
    CustomLoopExecutionLeaseResult TryReserveWorkspaceBusyOutcome(string operationId, string requestHash);

    /// <summary>
    /// Releases host ownership so a waiting custom-loop execution may proceed.
    /// </summary>
    void RelinquishWorkspaceHost();
}

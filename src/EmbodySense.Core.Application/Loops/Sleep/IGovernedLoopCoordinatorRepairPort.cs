using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Owns append-only coordinator-repair dispositions and their fenced fresh-acquisition boundary.</summary>
public interface IGovernedLoopCoordinatorRepairPort
{
    /// <summary>Reads the latest repair disposition that exactly names one failed ownership generation.</summary>
    Task<GovernedLoopCoordinatorRepairReadResult?> ReadAsync(
        string coordinatorId,
        string failedOwnershipHash,
        CancellationToken cancellationToken = default);

    /// <summary>Appends or exactly replays one immutable operator repair disposition through the coordinator-ledger CAS.</summary>
    Task<GovernedLoopCoordinatorRepairMutationResult?> AppendAsync(
        GovernedLoopCoordinatorRepairDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>Acquires a fresh fenced coordinator generation only against one retained exact repair disposition.</summary>
    Task<GovernedLoopCoordinatorAcquisitionResult?> TryAcquireAfterRepairAsync(
        GovernedLoopCoordinatorRepairAcquisitionRequest request,
        CancellationToken cancellationToken = default);
}

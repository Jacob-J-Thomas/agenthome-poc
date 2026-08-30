using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Binds one fresh acquisition to the exact retained failed generation and authenticated repair disposition that permits it.</summary>
public sealed record GovernedLoopCoordinatorRepairAcquisitionRequest(
    GovernedLoopCoordinatorRepairDisposition Repair,
    GovernedLoopCoordinatorAcquisitionRequest Acquisition)
{
    /// <summary>Gets a detached immutable repair disposition.</summary>
    public GovernedLoopCoordinatorRepairDisposition Repair { get; } = Repair with { };

    /// <summary>Gets a detached normal acquisition request.</summary>
    public GovernedLoopCoordinatorAcquisitionRequest Acquisition { get; } = new(
        Acquisition.PriorEvidenceExpectation,
        Acquisition.ExpectedOwnershipHash,
        Acquisition.ExpectedHeartbeatHash,
        Acquisition.ProposedOwnership,
        Acquisition.StartingLifecycle,
        Acquisition.InitialHeartbeat);
}

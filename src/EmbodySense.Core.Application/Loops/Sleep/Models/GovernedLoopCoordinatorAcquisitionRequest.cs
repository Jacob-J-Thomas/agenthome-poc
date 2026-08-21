using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Requests one atomic initial acquisition or lease-expired ownership handoff.</summary>
/// <param name="PriorEvidenceExpectation">Whether prior evidence must be absent or match exact hashes.</param>
/// <param name="ExpectedOwnershipHash">The exact prior ownership hash for a handoff.</param>
/// <param name="ExpectedHeartbeatHash">The exact prior heartbeat hash for a handoff.</param>
/// <param name="ProposedOwnership">The proposed new ownership claim.</param>
/// <param name="StartingLifecycle">The version-one starting lifecycle committed with ownership.</param>
/// <param name="InitialHeartbeat">The sequence-one heartbeat committed with ownership.</param>
public sealed record GovernedLoopCoordinatorAcquisitionRequest(
    GovernedLoopCoordinatorPriorEvidenceExpectation PriorEvidenceExpectation,
    string? ExpectedOwnershipHash,
    string? ExpectedHeartbeatHash,
    GovernedLoopCoordinatorOwnership ProposedOwnership,
    GovernedLoopCoordinatorLifecycle StartingLifecycle,
    GovernedLoopCoordinatorHeartbeat InitialHeartbeat)
{
    /// <summary>Gets a detached proposed ownership claim.</summary>
    public GovernedLoopCoordinatorOwnership ProposedOwnership { get; } = GovernedLoopCoordinatorEvidenceCopy.Ownership(ProposedOwnership);

    /// <summary>Gets a detached starting lifecycle state.</summary>
    public GovernedLoopCoordinatorLifecycle StartingLifecycle { get; } = GovernedLoopCoordinatorEvidenceCopy.Lifecycle(StartingLifecycle);

    /// <summary>Gets a detached initial heartbeat.</summary>
    public GovernedLoopCoordinatorHeartbeat InitialHeartbeat { get; } = GovernedLoopCoordinatorEvidenceCopy.Heartbeat(InitialHeartbeat);
}

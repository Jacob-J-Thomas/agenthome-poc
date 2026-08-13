using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Requests one fenced contiguous heartbeat renewal.</summary>
/// <param name="ExpectedOwnership">The exact ownership claim expected to remain authoritative.</param>
/// <param name="ExpectedOwnershipHash">The exact ownership compare-and-swap hash.</param>
/// <param name="ExpectedHeartbeatSequence">The exact prior heartbeat sequence.</param>
/// <param name="ExpectedHeartbeatHash">The exact prior heartbeat hash.</param>
/// <param name="ProposedHeartbeat">The proposed contiguous heartbeat successor.</param>
public sealed record GovernedLoopCoordinatorHeartbeatMutationRequest(
    GovernedLoopCoordinatorOwnership ExpectedOwnership,
    string ExpectedOwnershipHash,
    long ExpectedHeartbeatSequence,
    string ExpectedHeartbeatHash,
    GovernedLoopCoordinatorHeartbeat ProposedHeartbeat)
{
    /// <summary>Gets a detached expected ownership claim.</summary>
    public GovernedLoopCoordinatorOwnership ExpectedOwnership { get; } = GovernedLoopCoordinatorEvidenceCopy.Ownership(ExpectedOwnership);

    /// <summary>Gets a detached proposed heartbeat.</summary>
    public GovernedLoopCoordinatorHeartbeat ProposedHeartbeat { get; } = GovernedLoopCoordinatorEvidenceCopy.Heartbeat(ProposedHeartbeat);
}

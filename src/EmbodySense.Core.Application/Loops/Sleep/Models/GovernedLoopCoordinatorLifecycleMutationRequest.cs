using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Requests one fenced contiguous lifecycle append.</summary>
/// <param name="ExpectedOwnership">The exact ownership claim expected to remain authoritative.</param>
/// <param name="ExpectedOwnershipHash">The exact ownership compare-and-swap hash.</param>
/// <param name="ExpectedLifecycleVersion">The exact prior lifecycle version.</param>
/// <param name="ExpectedLifecycleHash">The exact prior lifecycle hash.</param>
/// <param name="ProposedLifecycle">The proposed contiguous lifecycle successor.</param>
public sealed record GovernedLoopCoordinatorLifecycleMutationRequest(
    GovernedLoopCoordinatorOwnership ExpectedOwnership,
    string ExpectedOwnershipHash,
    long ExpectedLifecycleVersion,
    string ExpectedLifecycleHash,
    GovernedLoopCoordinatorLifecycle ProposedLifecycle)
{
    /// <summary>Gets a detached expected ownership claim.</summary>
    public GovernedLoopCoordinatorOwnership ExpectedOwnership { get; } = GovernedLoopCoordinatorEvidenceCopy.Ownership(ExpectedOwnership);

    /// <summary>Gets a detached proposed lifecycle state.</summary>
    public GovernedLoopCoordinatorLifecycle ProposedLifecycle { get; } = GovernedLoopCoordinatorEvidenceCopy.Lifecycle(ProposedLifecycle);
}

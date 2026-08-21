using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Requests one fenced contiguous failure append.</summary>
/// <param name="ExpectedOwnership">The exact ownership claim expected to remain authoritative.</param>
/// <param name="ExpectedOwnershipHash">The exact ownership compare-and-swap hash.</param>
/// <param name="PriorFailureExpectation">Whether no failure or one exact prior failure is expected.</param>
/// <param name="ExpectedFailureSequence">Zero for no prior failure, otherwise the exact prior sequence.</param>
/// <param name="ExpectedFailureHash">Absent for no prior failure, otherwise the exact prior hash.</param>
/// <param name="ProposedFailure">The proposed contiguous failure successor.</param>
public sealed record GovernedLoopCoordinatorFailureMutationRequest(
    GovernedLoopCoordinatorOwnership ExpectedOwnership,
    string ExpectedOwnershipHash,
    GovernedLoopCoordinatorPriorFailureExpectation PriorFailureExpectation,
    long ExpectedFailureSequence,
    string? ExpectedFailureHash,
    GovernedLoopCoordinatorFailure ProposedFailure)
{
    /// <summary>Gets a detached expected ownership claim.</summary>
    public GovernedLoopCoordinatorOwnership ExpectedOwnership { get; } = GovernedLoopCoordinatorEvidenceCopy.Ownership(ExpectedOwnership);

    /// <summary>Gets a detached proposed failure.</summary>
    public GovernedLoopCoordinatorFailure ProposedFailure { get; } = GovernedLoopCoordinatorEvidenceCopy.Failure(ProposedFailure);
}

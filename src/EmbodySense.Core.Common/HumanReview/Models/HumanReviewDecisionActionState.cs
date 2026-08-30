using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Retains one append-only reservation, wake, claim, and terminal result chain for an accepted non-approval decision.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="Reservation">The immutable reservation bound to the accepted Reject, Cancel, or RequestInformation decision.</param>
/// <param name="BindingHash">The exact immutable reviewed run, revision, frontier, and activation binding hash.</param>
/// <param name="ExpectedGeneration">The exact immutable execution generation.</param>
/// <param name="ReservedLifecycleVersion">The complete run lifecycle version that first retained this action reservation.</param>
/// <param name="Wake">The one published action wake, or null while publication has not completed.</param>
/// <param name="Claims">The append-only bounded worker-claim history.</param>
/// <param name="Completion">The one conclusive action completion, if any.</param>
/// <param name="Retirement">The one fail-closed non-completion retirement, if any.</param>
/// <param name="StateHash">The canonical hash of all behavior-affecting action state.</param>
public sealed record HumanReviewDecisionActionState(int SchemaVersion, HumanReviewDecisionActionReservation Reservation, string BindingHash, long ExpectedGeneration, int ReservedLifecycleVersion, HumanReviewDecisionActionWake? Wake, ImmutableArray<HumanReviewDecisionActionClaim> Claims, HumanReviewDecisionActionCompletion? Completion, HumanReviewDecisionActionRetirement? Retirement, string StateHash)
{
    /// <summary>Gets the only supported action-state schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}

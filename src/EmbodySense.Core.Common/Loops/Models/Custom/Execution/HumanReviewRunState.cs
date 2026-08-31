using System.Collections.Immutable;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>Persists the current immutable Human Review request plus bounded completed review snapshots and append-only evidence inside the canonical run artifact.</summary>
/// <param name="Request">The exact immutable request bound to the parked frontier.</param>
/// <param name="Lifecycle">The initial pending lifecycle head.</param>
/// <param name="Evidence">The ordered append-only review evidence chain.</param>
public sealed record HumanReviewRunState(
    [property: JsonRequired] HumanReviewRequest Request,
    [property: JsonRequired] HumanReviewLifecycle Lifecycle,
    [property: JsonRequired] ImmutableArray<HumanReviewEvidence> Evidence)
{
    /// <summary>Gets the bounded append-only lifecycle chain beginning with the admitted pending head.</summary>
    [JsonRequired]
    public ImmutableArray<HumanReviewLifecycle> LifecycleHistory { get; init; } = [Lifecycle];

    /// <summary>Gets the bounded append-only decision-operation receipt ledger.</summary>
    [JsonRequired]
    public ImmutableArray<HumanReviewDecisionOperationReceipt> OperationReceipts { get; init; } = ImmutableArray<HumanReviewDecisionOperationReceipt>.Empty;

    /// <summary>Gets the ordered accepted decisions, including zero or more information requests and at most one terminal decision.</summary>
    [JsonRequired]
    public ImmutableArray<HumanReviewDecision> AcceptedDecisions { get; init; } = ImmutableArray<HumanReviewDecision>.Empty;

    /// <summary>Gets the one accepted terminal decision, or null while no terminal decision has been accepted.</summary>
    [JsonRequired]
    public HumanReviewDecision? AcceptedTerminalDecision { get; init; }

    /// <summary>Gets the one approval continuation reservation, or null unless the accepted terminal decision is an approval.</summary>
    [JsonRequired]
    public HumanReviewContinuationReservation? ContinuationReservation { get; init; }

    /// <summary>Gets the append-only continuation wake, claim, and terminal-result state, or null until an accepted approval is published.</summary>
    /// <remarks>The canonical run artifact is the only authority for this state. A non-null value is validated against the exact request and approval reservation.</remarks>
    [JsonRequired]
    public HumanReviewContinuationState? Continuation { get; init; }

    /// <summary>Gets the append-only action chains reserved for accepted Reject, Cancel, and RequestInformation decisions.</summary>
    /// <remarks>These chains are disjoint from approval continuation state and cannot contain approval or effect-release evidence.</remarks>
    [JsonRequired]
    public ImmutableArray<HumanReviewDecisionActionState> DecisionActions { get; init; } = ImmutableArray<HumanReviewDecisionActionState>.Empty;

    /// <summary>Gets the bounded append-only terminal review snapshots retained before the current review boundary.</summary>
    /// <remarks>
    /// Each entry is a complete terminal state with an empty <see cref="CompletedReviews"/> collection. The current request
    /// remains the only mutable review head; completed entries are retained for replay, audit, and exact sequential re-entry.
    /// </remarks>
    [JsonRequired]
    public ImmutableArray<HumanReviewRunState> CompletedReviews { get; init; } = ImmutableArray<HumanReviewRunState>.Empty;
}

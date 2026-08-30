using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Defines bounded canonical discovery, reread, and compare-exchange mutation for non-approval decision actions.</summary>
public interface IHumanReviewDecisionActionRecoveryStore : IHumanReviewDecisionActionPublicationStore
{
    /// <summary>Discovers one bounded page of strict-expiry claimable canonical action wakes.</summary>
    Task<HumanReviewDecisionActionRecoveryPage> ListCandidatesAsync(int maximumCount, string? scanCursor, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Rereads an exact claimed action candidate with its current graph and run evidence.</summary>
    Task<HumanReviewDecisionActionCandidateReadResult> ReadAsync(HumanReviewDecisionActionCandidateQuery query, CancellationToken cancellationToken = default);

    /// <summary>Atomically appends one exact worker claim when the candidate lifecycle fence remains current.</summary>
    Task<HumanReviewDecisionActionStoreMutationResult> ClaimAsync(HumanReviewDecisionActionClaimIntent intent, CancellationToken cancellationToken = default);

    /// <summary>Atomically records one conclusive claimed action completion.</summary>
    Task<HumanReviewDecisionActionStoreMutationResult> CompleteAsync(HumanReviewDecisionActionCompletionIntent intent, HumanReviewDecisionActionCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>Atomically records one fail-closed action retirement.</summary>
    Task<HumanReviewDecisionActionStoreMutationResult> RetireAsync(HumanReviewDecisionActionRetirementIntent intent, HumanReviewDecisionActionRetirement retirement, CancellationToken cancellationToken = default);
}

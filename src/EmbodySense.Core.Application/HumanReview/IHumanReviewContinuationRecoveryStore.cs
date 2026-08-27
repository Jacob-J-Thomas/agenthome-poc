using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Defines the canonical host-neutral port for bounded discovery and fenced mutation of accepted Human Review continuations.</summary>
/// <remarks>Implementations must use canonical run state, preserve opaque exclusive scan cursors, and never maintain a second continuation ledger or queue.</remarks>
public interface IHumanReviewContinuationRecoveryStore : IHumanReviewContinuationCandidateSource
{
    /// <summary>Discovers one bounded page of eligible accepted continuation candidates from canonical state.</summary>
    /// <param name="maximumCount">The maximum canonical run summaries to scan.</param>
    /// <param name="scanCursor">The opaque exclusive cursor from the preceding scan, or null to start at the beginning.</param>
    /// <param name="observedAtUtc">The trusted UTC instant used only to classify strict claim expiry.</param>
    /// <param name="cancellationToken">Cancels the bounded discovery operation.</param>
    /// <returns>A closed page result whose cursor advances through every nonempty source page, even when filtering produces no candidates.</returns>
    Task<HumanReviewContinuationRecoveryPage> ListCandidatesAsync(int maximumCount, string? scanCursor, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Atomically appends one exact worker claim to a current eligible candidate.</summary>
    /// <param name="intent">The candidate lifecycle fence and exact canonical claim.</param>
    /// <param name="cancellationToken">Cancels before a definitive atomic outcome is available.</param>
    /// <returns>A closed canonical mutation posture.</returns>
    Task<HumanReviewContinuationStoreMutationResult> ClaimAsync(HumanReviewContinuationClaimIntent intent, CancellationToken cancellationToken = default);

    /// <summary>Atomically records one conclusive completion through the exact current claim fence.</summary>
    /// <param name="intent">The Application completion precondition emitted for the prepared release.</param>
    /// <param name="completion">The conclusive release receipt and completion evidence from the host-neutral release port.</param>
    /// <param name="cancellationToken">Cancels before a definitive atomic outcome is available.</param>
    /// <returns>A closed canonical mutation posture.</returns>
    Task<HumanReviewContinuationStoreMutationResult> CompleteAsync(HumanReviewContinuationCompletionIntent intent, HumanReviewContinuationCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>Atomically records one fail-closed retirement through the exact current claim fence.</summary>
    /// <param name="intent">The Application retirement precondition emitted by continuation evaluation.</param>
    /// <param name="retirement">The canonical terminal non-completion artifact.</param>
    /// <param name="cancellationToken">Cancels before a definitive atomic outcome is available.</param>
    /// <returns>A closed canonical mutation posture.</returns>
    Task<HumanReviewContinuationStoreMutationResult> RetireAsync(HumanReviewContinuationRetirementIntent intent, HumanReviewContinuationRetirement retirement, CancellationToken cancellationToken = default);
}

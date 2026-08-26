using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Defines the canonical Application port for rereading and terminally recording exact Human Review continuation work.</summary>
/// <remarks>Implementations own the canonical compare-exchange boundary. They must not maintain a second authoritative queue, treat a consumer intent as dispatch permission, or turn response loss into a duplicate callback.</remarks>
public interface IHumanReviewContinuationStore : IHumanReviewContinuationCandidateSource
{
    /// <summary>Records a completion only when the exact claimed generation and canonical successor still match.</summary>
    /// <param name="intent">The exact completion precondition emitted by the Application consumer.</param>
    /// <param name="cancellationToken">Cancels before the atomic mutation completes.</param>
    /// <returns>A closed committed, replayed, conflict, missing, invalid, unavailable, or limit result.</returns>
    Task<HumanReviewContinuationStoreMutationResult> CompleteAsync(HumanReviewContinuationCompletionIntent intent, CancellationToken cancellationToken = default);

    /// <summary>Records one fail-closed retirement only when the exact wake, reservation, and generation still match.</summary>
    /// <param name="intent">The exact retirement request emitted by the Application consumer.</param>
    /// <param name="cancellationToken">Cancels before the atomic mutation completes.</param>
    /// <returns>A closed committed, replayed, conflict, missing, invalid, unavailable, or limit result.</returns>
    Task<HumanReviewContinuationStoreMutationResult> RetireAsync(HumanReviewContinuationRetirementIntent intent, CancellationToken cancellationToken = default);
}

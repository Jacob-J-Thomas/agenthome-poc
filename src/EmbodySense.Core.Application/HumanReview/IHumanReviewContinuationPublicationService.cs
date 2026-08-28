using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Publishes the one deterministic continuation wake derived from a durably accepted Human Review approval reservation.</summary>
public interface IHumanReviewContinuationPublicationService
{
    /// <summary>Rereads canonical run state and publishes or exactly replays its accepted approval continuation.</summary>
    /// <param name="runId">The exact canonical run identity carrying the accepted reservation.</param>
    /// <param name="cancellationToken">Cancels before a definitive publication result is available.</param>
    /// <returns>A closed committed, replayed, conflict, missing, invalid, unavailable, or quota posture.</returns>
    Task<HumanReviewContinuationStoreMutationResult> PublishAsync(string runId, CancellationToken cancellationToken = default);
}

using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Publishes one exact wake-only Human Review continuation through the canonical whole-run compare-exchange boundary.</summary>
public interface IHumanReviewContinuationPublicationStore
{
    /// <summary>Publishes or exactly replays a wake-only continuation for one accepted approval reservation.</summary>
    /// <param name="runId">The exact canonical run identity.</param>
    /// <param name="expectedLifecycleVersion">The whole-run version observed before construction.</param>
    /// <param name="continuation">The canonical wake-only continuation state.</param>
    /// <param name="cancellationToken">Cancels before a definitive atomic outcome is available.</param>
    /// <returns>A closed canonical publication posture.</returns>
    Task<HumanReviewContinuationStoreMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewContinuationState continuation, CancellationToken cancellationToken = default);
}

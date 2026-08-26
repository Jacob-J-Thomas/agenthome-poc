using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Independently revalidates current release authority for an exact Human Review continuation without dispatching it.</summary>
public interface IHumanReviewContinuationAuthoritySource
{
    /// <summary>Re-reads current authority and all named non-effect release evidence for one exact binding.</summary>
    /// <remarks>Implementations must use canonical current sources and fail closed. They must not treat the review as authority, restore or widen grants, acquire a lease, or cross an irreversible boundary.</remarks>
    /// <param name="query">The complete exact reviewed, admitted, and graph-pinned authority query.</param>
    /// <param name="cancellationToken">Cancels revalidation before it completes.</param>
    /// <returns>A closed current, narrowed, revoked, stale, unavailable, or invalid posture.</returns>
    Task<HumanReviewContinuationAuthorityReadResult> ReadAsync(HumanReviewContinuationAuthorityQuery query, CancellationToken cancellationToken = default);
}

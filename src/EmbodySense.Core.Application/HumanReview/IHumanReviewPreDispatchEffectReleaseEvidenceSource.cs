using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Re-reads server-owned durable evidence before one reviewed effect may cross its dispatch boundary.</summary>
/// <remarks>Implementations must treat every query field as an expectation only and derive authority solely from current canonical persistence.</remarks>
public interface IHumanReviewPreDispatchEffectReleaseEvidenceSource
{
    /// <summary>Determines whether one claimed release is the exact current canonical release for its retained effect attempt.</summary>
    /// <param name="query">The untrusted release expectation and exact retained effect-attempt coordinates.</param>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>The closed current-evidence posture.</returns>
    Task<HumanReviewPreDispatchEffectReleaseEvidenceReadStatus> ReadReleasedAsync(
        HumanReviewPreDispatchEffectReleaseEvidenceQuery query,
        CancellationToken cancellationToken = default);
}

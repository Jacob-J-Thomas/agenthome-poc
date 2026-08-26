using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Accepts one bounded Human Review decision proposal through authenticated, trusted server dependencies.</summary>
public interface IHumanReviewDecisionService
{
    /// <summary>Records one decision operation or returns its exact authenticated replay.</summary>
    /// <param name="command">The bounded untrusted proposal and expected run version.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The store outcome; approval only reserves a continuation and never releases it.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Thrown when cancellation prevents completion and no exact durable operation can be reconciled.</exception>
    Task<HumanReviewDecisionServiceResult> DecideAsync(HumanReviewDecisionCommand command, CancellationToken cancellationToken = default);
}

using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Consumes one detached canonical Human Review candidate into a fail-closed declared action, completion precondition, or retirement intent.</summary>
public interface IHumanReviewContinuationConsumer
{
    /// <summary>Evaluates one re-read canonical candidate without persisting, claiming, completing, retiring, resuming, or dispatching it.</summary>
    /// <remarks>Callers must invoke this at most once for a successful exact claim and apply returned intents through the canonical compare-exchange boundary. Effect execution must revalidate again immediately before its irreversible boundary.</remarks>
    /// <param name="candidate">The detached current canonical candidate.</param>
    /// <param name="cancellationToken">Cancels evaluation before a result can be returned.</param>
    /// <returns>A closed fail-closed action, completion precondition, retirement, invalid, or unavailable result.</returns>
    Task<HumanReviewContinuationConsumptionResult> ConsumeAsync(HumanReviewContinuationCandidate candidate, CancellationToken cancellationToken = default);
}

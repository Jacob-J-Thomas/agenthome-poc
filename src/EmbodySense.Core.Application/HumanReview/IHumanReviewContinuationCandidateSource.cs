using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Rereads one exact Human Review decision or claim candidate from canonical durable state without acquiring a lease or releasing work.</summary>
public interface IHumanReviewContinuationCandidateSource
{
    /// <summary>Loads one detached candidate that exactly matches the supplied immutable query.</summary>
    /// <remarks>Implementations must read the canonical run, review, frontier, graph, and effect evidence rather than echoing caller data. They must not create, claim, complete, retire, resume, or dispatch a continuation.</remarks>
    /// <param name="query">The exact request, decision, and optional approved-continuation references to reread.</param>
    /// <param name="cancellationToken">Cancels the bounded read before it completes.</param>
    /// <returns>A closed current, missing, corrupt, stale, or unavailable result.</returns>
    Task<HumanReviewContinuationCandidateReadResult> ReadAsync(HumanReviewContinuationCandidateQuery query, CancellationToken cancellationToken = default);
}

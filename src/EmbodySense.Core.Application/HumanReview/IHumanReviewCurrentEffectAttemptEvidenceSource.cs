using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Rereads the exact current server-derived effect-attempt identity and preparation evidence needed to construct a Human Review certainty query.</summary>
public interface IHumanReviewCurrentEffectAttemptEvidenceSource
{
    /// <summary>Reads one bounded detached current effect-attempt identity and preparation from canonical state.</summary>
    /// <remarks>Implementations must derive returned evidence from the canonical retained attempt and must never call transition, resume, lease-acquisition, or dispatch APIs. They must not echo caller values as proof or expose raw effect payloads.</remarks>
    /// <param name="query">The exact reviewed binding and effect-attempt reference to reread.</param>
    /// <param name="cancellationToken">Cancels the bounded read before it completes.</param>
    /// <returns>A closed current, missing, corrupt, stale, or unavailable result.</returns>
    Task<HumanReviewCurrentEffectAttemptEvidenceReadResult> ReadAsync(HumanReviewCurrentEffectAttemptEvidenceQuery query, CancellationToken cancellationToken = default);
}

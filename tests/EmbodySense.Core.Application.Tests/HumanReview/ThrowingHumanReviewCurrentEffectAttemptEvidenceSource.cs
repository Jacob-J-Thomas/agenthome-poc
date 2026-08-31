using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class ThrowingHumanReviewCurrentEffectAttemptEvidenceSource(Exception exception) : IHumanReviewCurrentEffectAttemptEvidenceSource
{
    public Task<HumanReviewCurrentEffectAttemptEvidenceReadResult> ReadAsync(HumanReviewCurrentEffectAttemptEvidenceQuery query, CancellationToken cancellationToken = default)
        => Task.FromException<HumanReviewCurrentEffectAttemptEvidenceReadResult>(exception);
}

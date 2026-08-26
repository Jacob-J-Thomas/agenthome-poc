using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class RecordingEffectEvidenceSource(params HumanReviewCurrentEffectAttemptEvidenceReadResult[] results) : IHumanReviewCurrentEffectAttemptEvidenceSource
{
    private readonly Queue<HumanReviewCurrentEffectAttemptEvidenceReadResult> _results = new(results);

    public int ReadCount { get; private set; }

    public List<HumanReviewCurrentEffectAttemptEvidenceQuery> Queries { get; } = [];

    public Task<HumanReviewCurrentEffectAttemptEvidenceReadResult> ReadAsync(HumanReviewCurrentEffectAttemptEvidenceQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        Queries.Add(query);
        return Task.FromResult(_results.Count == 0 ? new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable) : _results.Dequeue());
    }
}

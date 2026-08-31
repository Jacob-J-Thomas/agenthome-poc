using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Effects;

internal sealed class StubHumanReviewReleaseEvidenceSource(params HumanReviewPreDispatchEffectReleaseEvidenceReadStatus[] statuses) : IHumanReviewPreDispatchEffectReleaseEvidenceSource
{
    private readonly Queue<HumanReviewPreDispatchEffectReleaseEvidenceReadStatus> _statuses = new(statuses.Length == 0 ? [HumanReviewPreDispatchEffectReleaseEvidenceReadStatus.Missing] : statuses);

    internal int Calls { get; private set; }

    public Task<HumanReviewPreDispatchEffectReleaseEvidenceReadStatus> ReadReleasedAsync(
        HumanReviewPreDispatchEffectReleaseEvidenceQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);
        Calls++;
        var status = _statuses.Count > 1 ? _statuses.Dequeue() : _statuses.Peek();
        return Task.FromResult(status);
    }
}

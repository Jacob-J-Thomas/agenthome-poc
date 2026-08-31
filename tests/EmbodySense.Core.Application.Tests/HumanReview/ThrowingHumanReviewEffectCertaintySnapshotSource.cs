using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class ThrowingHumanReviewEffectCertaintySnapshotSource(Exception exception) : IGovernedLoopEffectCertaintySnapshotSource
{
    public Task<GovernedLoopEffectCertaintySnapshotResult> ReadAsync(GovernedLoopEffectCertaintySnapshotQuery query, CancellationToken cancellationToken = default)
        => Task.FromException<GovernedLoopEffectCertaintySnapshotResult>(exception);
}

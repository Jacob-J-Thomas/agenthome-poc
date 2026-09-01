using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryRecordingDecisionActionRelease : IHumanReviewDecisionActionReleasePort
{
    public Task<HumanReviewDecisionActionReleaseResult> ReleaseAsync(HumanReviewDecisionActionIntent intent, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanReviewDecisionActionReleaseResult(HumanReviewDecisionActionReleaseStatus.Unavailable));
}

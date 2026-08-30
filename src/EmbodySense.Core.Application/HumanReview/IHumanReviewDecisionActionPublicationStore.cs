using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Publishes one exact wake-only non-approval action through the canonical whole-run compare-exchange boundary.</summary>
public interface IHumanReviewDecisionActionPublicationStore
{
    /// <summary>Publishes or exactly replays a wake-only action state at the supplied run version.</summary>
    Task<HumanReviewDecisionActionStoreMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewDecisionActionState action, CancellationToken cancellationToken = default);
}

using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Publishes one deterministic wake for a retained non-approval Human Review action reservation.</summary>
public interface IHumanReviewDecisionActionPublicationService
{
    /// <summary>Publishes or exactly replays the named canonical action wake.</summary>
    Task<HumanReviewDecisionActionStoreMutationResult> PublishAsync(HumanReviewDecisionActionPublicationCommand command, CancellationToken cancellationToken = default);
}

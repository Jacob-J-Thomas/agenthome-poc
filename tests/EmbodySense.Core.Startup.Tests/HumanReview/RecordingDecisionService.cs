using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using ApplicationDecisionResult = EmbodySense.Core.Application.HumanReview.Models.HumanReviewDecisionServiceResult;
using ApplicationDecisionStatus = EmbodySense.Core.Application.HumanReview.Models.HumanReviewDecisionServiceStatus;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

internal sealed class RecordingDecisionService : IHumanReviewDecisionService
{
    public ApplicationDecisionResult Result { get; init; } = new(ApplicationDecisionStatus.Unavailable, null);
    public HumanReviewDecisionCommand? Command { get; private set; }

    public Task<ApplicationDecisionResult> DecideAsync(HumanReviewDecisionCommand command, CancellationToken cancellationToken = default)
    {
        Command = command;
        return Task.FromResult(Result);
    }
}

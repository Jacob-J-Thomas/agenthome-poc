using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryRecordingPublicationService : IHumanReviewContinuationPublicationService
{
    public Func<string, HumanReviewContinuationStoreMutationStatus>? StatusFactory { get; init; }
    public Func<string, Exception?>? ExceptionFactory { get; init; }
    public List<string> Calls { get; } = [];

    public Task<HumanReviewContinuationStoreMutationResult> PublishAsync(string runId, CancellationToken cancellationToken = default)
    {
        Calls.Add(runId);
        if (ExceptionFactory?.Invoke(runId) is { } exception)
        {
            throw exception;
        }

        return Task.FromResult(new HumanReviewContinuationStoreMutationResult(StatusFactory?.Invoke(runId) ?? HumanReviewContinuationStoreMutationStatus.Unavailable));
    }
}

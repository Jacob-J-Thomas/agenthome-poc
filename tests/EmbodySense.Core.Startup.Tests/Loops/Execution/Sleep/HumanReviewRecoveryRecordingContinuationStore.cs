using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryRecordingContinuationStore : IHumanReviewContinuationRecoveryStore
{
    public Func<HumanReviewContinuationRecoveryRequest, HumanReviewContinuationRecoveryPage>? PageFactory { get; init; }
    public List<string?> Cursors { get; } = [];
    public HumanReviewContinuationStoreMutationResult ClaimResult { get; init; } = new(HumanReviewContinuationStoreMutationStatus.Unavailable);
    public HumanReviewContinuationCandidateReadResult ReadResult { get; init; } = new(HumanReviewContinuationCandidateReadStatus.Missing);

    public Task<HumanReviewContinuationRecoveryPage> ListCandidatesAsync(int maximumCount, string? scanCursor, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
    {
        Cursors.Add(scanCursor);
        return Task.FromResult(PageFactory?.Invoke(new(maximumCount, scanCursor, "worker-a", "source-a", TimeSpan.FromMinutes(2))) ?? new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [], null, false));
    }
    public Task<HumanReviewContinuationCandidateReadResult> ReadAsync(HumanReviewContinuationCandidateQuery query, CancellationToken cancellationToken = default) => Task.FromResult(ReadResult);
    public Task<HumanReviewContinuationStoreMutationResult> ClaimAsync(HumanReviewContinuationClaimIntent intent, CancellationToken cancellationToken = default) => Task.FromResult(ClaimResult);
    public Task<HumanReviewContinuationStoreMutationResult> CompleteAsync(HumanReviewContinuationCompletionIntent intent, HumanReviewContinuationCompletion completion, CancellationToken cancellationToken = default) => Task.FromResult(new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Unavailable));
    public Task<HumanReviewContinuationStoreMutationResult> RetireAsync(HumanReviewContinuationRetirementIntent intent, HumanReviewContinuationRetirement retirement, CancellationToken cancellationToken = default) => Task.FromResult(new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Unavailable));
}

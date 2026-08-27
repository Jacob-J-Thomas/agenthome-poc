using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewContinuationRecoveryTestStore(
    HumanReviewContinuationRecoveryPage page,
    HumanReviewContinuationCandidateReadResult reread) : IHumanReviewContinuationRecoveryStore
{
    public int ClaimCount { get; private set; }
    public int ReadCount { get; private set; }
    public int CompleteCount { get; private set; }
    public int RetireCount { get; private set; }
    public HumanReviewContinuationClaimIntent? LastClaim { get; private set; }
    public HumanReviewContinuationCandidateQuery? LastRead { get; private set; }
    public HumanReviewContinuationStoreMutationResult ClaimResult { get; init; } = new(HumanReviewContinuationStoreMutationStatus.Committed);
    public HumanReviewContinuationStoreMutationResult CompleteResult { get; init; } = new(HumanReviewContinuationStoreMutationStatus.Committed);
    public HumanReviewContinuationStoreMutationResult RetireResult { get; init; } = new(HumanReviewContinuationStoreMutationStatus.Committed);
    public Exception? ListException { get; init; }
    public Exception? ReadException { get; init; }
    public Exception? ClaimException { get; init; }
    public Exception? CompleteException { get; init; }
    public Exception? RetireException { get; init; }

    public Task<HumanReviewContinuationRecoveryPage> ListCandidatesAsync(int maximumCount, string? scanCursor, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
        => ListException is null ? Task.FromResult(page) : Task.FromException<HumanReviewContinuationRecoveryPage>(ListException);

    public Task<HumanReviewContinuationCandidateReadResult> ReadAsync(HumanReviewContinuationCandidateQuery query, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        LastRead = query;
        return ReadException is null ? Task.FromResult(reread) : Task.FromException<HumanReviewContinuationCandidateReadResult>(ReadException);
    }

    public Task<HumanReviewContinuationStoreMutationResult> ClaimAsync(HumanReviewContinuationClaimIntent intent, CancellationToken cancellationToken = default)
    {
        ClaimCount++;
        LastClaim = intent;
        return ClaimException is null ? Task.FromResult(ClaimResult) : Task.FromException<HumanReviewContinuationStoreMutationResult>(ClaimException);
    }

    public Task<HumanReviewContinuationStoreMutationResult> CompleteAsync(HumanReviewContinuationCompletionIntent intent, HumanReviewContinuationCompletion completion, CancellationToken cancellationToken = default)
    {
        CompleteCount++;
        return CompleteException is null ? Task.FromResult(CompleteResult) : Task.FromException<HumanReviewContinuationStoreMutationResult>(CompleteException);
    }

    public Task<HumanReviewContinuationStoreMutationResult> RetireAsync(HumanReviewContinuationRetirementIntent intent, HumanReviewContinuationRetirement retirement, CancellationToken cancellationToken = default)
    {
        RetireCount++;
        return RetireException is null ? Task.FromResult(RetireResult) : Task.FromException<HumanReviewContinuationStoreMutationResult>(RetireException);
    }
}

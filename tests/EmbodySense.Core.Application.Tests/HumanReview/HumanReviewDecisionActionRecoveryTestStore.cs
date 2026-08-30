using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewDecisionActionRecoveryTestStore(
    HumanReviewDecisionActionRecoveryPage page,
    HumanReviewDecisionActionCandidateReadResult reread) : IHumanReviewDecisionActionRecoveryStore
{
    public int ListCount { get; private set; }
    public int PublishCount { get; private set; }
    public int ClaimCount { get; private set; }
    public int ReadCount { get; private set; }
    public int CompleteCount { get; private set; }
    public int RetireCount { get; private set; }
    public HumanReviewDecisionActionClaimIntent? LastClaim { get; private set; }
    public HumanReviewDecisionActionCompletionIntent? LastCompletion { get; private set; }
    public HumanReviewDecisionActionRetirementIntent? LastRetirement { get; private set; }
    public List<HumanReviewDecisionActionCandidateQuery> ReadQueries { get; } = [];
    public HumanReviewDecisionActionStoreMutationResult ClaimResult { get; set; } = new(HumanReviewDecisionActionStoreMutationStatus.Committed);
    public HumanReviewDecisionActionStoreMutationResult CompleteResult { get; set; } = new(HumanReviewDecisionActionStoreMutationStatus.Committed);
    public HumanReviewDecisionActionStoreMutationResult RetireResult { get; set; } = new(HumanReviewDecisionActionStoreMutationStatus.Committed);
    public HumanReviewDecisionActionStoreMutationResult PublishResult { get; set; } = new(HumanReviewDecisionActionStoreMutationStatus.Committed);
    public Func<int, HumanReviewDecisionActionRecoveryPage>? PageFactory { get; set; }
    public Func<int, Exception?>? ListExceptionFactory { get; set; }
    public Exception? PublishException { get; set; }
    public Action? BeforePublish { get; set; }

    public Task<HumanReviewDecisionActionRecoveryPage> ListCandidatesAsync(int maximumCount, string? scanCursor, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
    {
        ListCount++;
        return ListExceptionFactory?.Invoke(ListCount) is { } exception
            ? Task.FromException<HumanReviewDecisionActionRecoveryPage>(exception)
            : Task.FromResult(PageFactory?.Invoke(ListCount) ?? page);
    }

    public Task<HumanReviewDecisionActionCandidateReadResult> ReadAsync(HumanReviewDecisionActionCandidateQuery query, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        ReadQueries.Add(query);
        return Task.FromResult(_actionHeadAdvanced && ReadCount > 1 ? new(HumanReviewDecisionActionCandidateReadStatus.Stale) : reread);
    }

    public void AdvanceActionHead() => _actionHeadAdvanced = true;

    public Task<HumanReviewDecisionActionStoreMutationResult> ClaimAsync(HumanReviewDecisionActionClaimIntent intent, CancellationToken cancellationToken = default)
    {
        ClaimCount++;
        LastClaim = intent;
        return Task.FromResult(ClaimResult);
    }

    public Task<HumanReviewDecisionActionStoreMutationResult> CompleteAsync(HumanReviewDecisionActionCompletionIntent intent, HumanReviewDecisionActionCompletion completion, CancellationToken cancellationToken = default)
    {
        CompleteCount++;
        LastCompletion = intent;
        return Task.FromResult(CompleteResult);
    }

    public Task<HumanReviewDecisionActionStoreMutationResult> RetireAsync(HumanReviewDecisionActionRetirementIntent intent, HumanReviewDecisionActionRetirement retirement, CancellationToken cancellationToken = default)
    {
        RetireCount++;
        LastRetirement = intent;
        return Task.FromResult(RetireResult);
    }

    public Task<HumanReviewDecisionActionStoreMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewDecisionActionState action, CancellationToken cancellationToken = default)
    {
        PublishCount++;
        BeforePublish?.Invoke();
        return PublishException is { } exception
            ? Task.FromException<HumanReviewDecisionActionStoreMutationResult>(exception)
            : Task.FromResult(PublishResult);
    }

    private bool _actionHeadAdvanced;
}

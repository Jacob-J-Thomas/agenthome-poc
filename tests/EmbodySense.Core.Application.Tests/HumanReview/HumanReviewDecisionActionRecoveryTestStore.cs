using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewDecisionActionRecoveryTestStore(
    HumanReviewDecisionActionRecoveryPage page,
    HumanReviewDecisionActionCandidateReadResult reread) : IHumanReviewDecisionActionRecoveryStore
{
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

    public Task<HumanReviewDecisionActionRecoveryPage> ListCandidatesAsync(int maximumCount, string? scanCursor, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default) => Task.FromResult(page);

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

    public Task<HumanReviewDecisionActionStoreMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewDecisionActionState action, CancellationToken cancellationToken = default) => Task.FromResult(new HumanReviewDecisionActionStoreMutationResult(HumanReviewDecisionActionStoreMutationStatus.Invalid));

    private bool _actionHeadAdvanced;
}

using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryRecordingDecisionActionStore : IHumanReviewDecisionActionRecoveryStore
{
    public Func<HumanReviewDecisionActionRecoveryRequest, HumanReviewDecisionActionRecoveryPage>? PageFactory { get; init; }
    public List<string?> Cursors { get; } = [];
    public HumanReviewDecisionActionStoreMutationResult ClaimResult { get; init; } = new(HumanReviewDecisionActionStoreMutationStatus.Unavailable);
    public HumanReviewDecisionActionCandidateReadResult ReadResult { get; init; } = new(HumanReviewDecisionActionCandidateReadStatus.Missing);

    public Task<HumanReviewDecisionActionRecoveryPage> ListCandidatesAsync(int maximumCount, string? scanCursor, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
    {
        Cursors.Add(scanCursor);
        return Task.FromResult(PageFactory?.Invoke(new(maximumCount, scanCursor, "worker-a", TimeSpan.FromMinutes(2))) ?? new HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus.Current, [], null, false));
    }
    public Task<HumanReviewDecisionActionCandidateReadResult> ReadAsync(HumanReviewDecisionActionCandidateQuery query, CancellationToken cancellationToken = default) => Task.FromResult(ReadResult);
    public Task<HumanReviewDecisionActionStoreMutationResult> ClaimAsync(HumanReviewDecisionActionClaimIntent intent, CancellationToken cancellationToken = default) => Task.FromResult(ClaimResult);
    public Task<HumanReviewDecisionActionStoreMutationResult> CompleteAsync(HumanReviewDecisionActionCompletionIntent intent, HumanReviewDecisionActionCompletion completion, CancellationToken cancellationToken = default) => Task.FromResult(new HumanReviewDecisionActionStoreMutationResult(HumanReviewDecisionActionStoreMutationStatus.Unavailable));
    public Task<HumanReviewDecisionActionStoreMutationResult> RetireAsync(HumanReviewDecisionActionRetirementIntent intent, HumanReviewDecisionActionRetirement retirement, CancellationToken cancellationToken = default) => Task.FromResult(new HumanReviewDecisionActionStoreMutationResult(HumanReviewDecisionActionStoreMutationStatus.Unavailable));
    public Task<HumanReviewDecisionActionStoreMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewDecisionActionState action, CancellationToken cancellationToken = default) => Task.FromResult(new HumanReviewDecisionActionStoreMutationResult(HumanReviewDecisionActionStoreMutationStatus.Unavailable));
}

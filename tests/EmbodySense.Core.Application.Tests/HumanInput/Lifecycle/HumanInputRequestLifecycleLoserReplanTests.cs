using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleLoserReplanTests
{
    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Amend)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reroute)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Supersede)]
    public async Task Same_head_winner_commits_loser_persists_optimistic_conflict_and_both_replay(
        HumanInputRequestLifecycleOperationKind kind)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
        if (kind == HumanInputRequestLifecycleOperationKind.Expire)
        {
            harness.Time.Value = original.Timing.ExpiresAtUtc.AddTicks(1);
        }

        var (winnerCandidate, loserCandidate) = Candidates(kind, original);
        var winner = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            kind,
            $"same-head-{kind.ToString().ToLowerInvariant()}-winner",
            original.RequestId,
            winnerCandidate);
        var loser = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            kind,
            $"same-head-{kind.ToString().ToLowerInvariant()}-loser",
            original.RequestId,
            loserCandidate);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);

        var winningResult = await harness.Service.MutateAsync(winner);
        var losingResult = await harness.Service.MutateAsync(loser);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, winningResult.Status);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, losingResult.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict, losingResult.Proof?.FailureCode);
        Assert.Equal(2, harness.Store.Commits.Count);

        var winningReplay = await harness.Service.MutateAsync(winner);
        var losingReplay = await harness.Service.MutateAsync(loser);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, winningReplay.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.None, winningReplay.Proof?.FailureCode);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, losingReplay.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict, losingReplay.Proof?.FailureCode);
        Assert.Equal(2, harness.Store.Commits.Count);
    }

    private static (HumanInputRequest? Winner, HumanInputRequest? Loser) Candidates(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequest original)
        => kind switch
        {
            HumanInputRequestLifecycleOperationKind.Amend => (
                HumanInputRequestLifecycleTransitionTestSupport.AmendCandidate(
                    original,
                    "amend-winner-version",
                    "Private winner amendment."),
                HumanInputRequestLifecycleTransitionTestSupport.AmendCandidate(
                    original,
                    "amend-loser-version",
                    "Private loser amendment.")),
            HumanInputRequestLifecycleOperationKind.Reroute => (
                HumanInputRequestLifecycleTransitionTestSupport.RerouteCandidate(
                    original,
                    "reroute-winner-version"),
                HumanInputRequestHash.Apply(original with
                {
                    RequestVersionId = "reroute-loser-version",
                    EligibleRespondents = [new HumanInputEligibleRespondent("user-three", "private-route-three")],
                    RequestHash = string.Empty,
                })),
            HumanInputRequestLifecycleOperationKind.Supersede => (
                HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(
                    original,
                    "request-winner",
                    "request-winner-version"),
                HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(
                    original,
                    "request-loser",
                    "request-loser-version")),
            _ => (null, null),
        };
}

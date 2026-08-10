using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleTimingTests
{
    [Fact]
    public async Task Create_accepts_normal_admission_delay_and_uses_server_resolution_time_for_evidence_and_head()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var candidate = HumanInputRequestLifecycleTestData.Request(
            requestedAtUtc: HumanInputRequestLifecycleTestData.Now.AddTicks(-1));
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "create-after-normal-delay",
            candidate.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            candidate);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, result.Status);
        var mutation = Assert.Single(harness.Store.Commits).Mutation;
        Assert.Equal(HumanInputRequestLifecycleTestData.Now, mutation.Operation.RecordedAtUtc);
        Assert.Equal(HumanInputRequestLifecycleTestData.Now, mutation.PrimaryHeadToWrite?.UpdatedAtUtc);
        Assert.Equal(candidate.Timing.RequestedAtUtc, mutation.RequestToAppend?.Timing.RequestedAtUtc);
        Assert.Equal(HumanInputRequestLifecycleTestData.Now, result.DeliveryOpportunity?.ProvedAtUtc);
    }

    [Fact]
    public async Task Supersede_accepts_normal_admission_delay_and_uses_server_resolution_time_for_both_heads()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var evaluatedAt = HumanInputRequestLifecycleTestData.Now.AddMinutes(1);
        harness.Resolver.Handler = (_, _) => HumanInputRequestLifecycleTestData.ActiveResolution(
            harness.Grant,
            evaluatedAt);
        var candidate = HumanInputRequestHash.Apply(
            HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(original) with
            {
                Timing = original.Timing with { RequestedAtUtc = evaluatedAt.AddTicks(-1) },
                RequestHash = string.Empty,
            });
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Supersede,
            "supersede-after-normal-delay",
            original.RequestId,
            candidate);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, result.Status);
        var mutation = Assert.Single(harness.Store.Commits).Mutation;
        Assert.Equal(evaluatedAt, mutation.Operation.RecordedAtUtc);
        Assert.Equal(evaluatedAt, mutation.PrimaryHeadToWrite?.UpdatedAtUtc);
        Assert.Equal(evaluatedAt, mutation.SecondaryHeadToWrite?.UpdatedAtUtc);
        Assert.Equal(candidate.Timing.RequestedAtUtc, mutation.RequestToAppend?.Timing.RequestedAtUtc);
        Assert.Equal(evaluatedAt, result.DeliveryOpportunity?.ProvedAtUtc);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Create, 0)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Create, 1)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Supersede, 0)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Supersede, 1)]
    public async Task Future_open_or_already_expired_candidate_window_persists_and_replays_timing_conflict(
        HumanInputRequestLifecycleOperationKind kind,
        int timingScenario)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        if (kind == HumanInputRequestLifecycleOperationKind.Supersede)
        {
            await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
            HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        }

        var requestedAt = timingScenario == 0
            ? HumanInputRequestLifecycleTestData.Now.AddTicks(1)
            : HumanInputRequestLifecycleTestData.Now.AddMinutes(-2);
        var expiresAt = timingScenario == 0
            ? HumanInputRequestLifecycleTestData.Now.AddHours(1)
            : HumanInputRequestLifecycleTestData.Now.AddMinutes(-1);
        var candidate = kind == HumanInputRequestLifecycleOperationKind.Create
            ? HumanInputRequestLifecycleTestData.Request(
                requestedAtUtc: requestedAt,
                expiresAtUtc: expiresAt)
            : HumanInputRequestHash.Apply(
                HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(original) with
                {
                    Timing = original.Timing with
                    {
                        RequestedAtUtc = requestedAt,
                        ExpiresAtUtc = expiresAt,
                    },
                    RequestHash = string.Empty,
                });
        var command = kind == HumanInputRequestLifecycleOperationKind.Create
            ? HumanInputRequestLifecycleTestData.Command(
                kind,
                $"create-invalid-window-{timingScenario}",
                candidate.RequestId,
                HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
                candidate)
            : HumanInputRequestLifecycleTransitionTestSupport.Command(
                harness,
                kind,
                $"supersede-invalid-window-{timingScenario}",
                original.RequestId,
                candidate);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            command,
            HumanInputRequestLifecycleMutationStatus.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict);
        Assert.Null(Assert.Single(harness.Store.Commits).Mutation.PrimaryHeadToWrite);
    }
}

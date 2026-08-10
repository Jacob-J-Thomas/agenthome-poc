using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleStaleDeliveryTests
{
    [Fact]
    public async Task Create_replay_after_cancellation_projects_current_terminal_head_without_stale_delivery()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        var create = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "create-before-stale-delivery-check",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            request);
        var created = await harness.Service.MutateAsync(create);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, created.Status);
        Assert.NotNull(created.DeliveryOpportunity);
        var cancel = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            "cancel-before-create-replay",
            request.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(cancel)).Status);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        RejectMutableDependencies(harness);

        var replay = await harness.Service.MutateAsync(create);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationOutcome.Committed, replay.Proof?.Outcome);
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, replay.Primary?.Status);
        Assert.Equal(2, replay.Primary?.LifecycleVersion);
        Assert.Null(replay.DeliveryOpportunity);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Remind)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reroute)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Amend)]
    public async Task Old_delivery_transition_replay_after_newer_head_suppresses_stale_opportunity(
        HumanInputRequestLifecycleOperationKind kind)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
        var candidate = Candidate(kind, original);
        var oldCommand = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            kind,
            $"old-{kind.ToString().ToLowerInvariant()}-delivery",
            original.RequestId,
            candidate);
        var first = await harness.Service.MutateAsync(oldCommand);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, first.Status);
        Assert.NotNull(first.DeliveryOpportunity);
        var freshReplay = await harness.Service.MutateAsync(oldCommand);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, freshReplay.Status);
        Assert.NotNull(freshReplay.DeliveryOpportunity);
        var newer = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            $"newer-head-after-{kind.ToString().ToLowerInvariant()}",
            original.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(newer)).Status);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        RejectMutableDependencies(harness);

        var staleReplay = await harness.Service.MutateAsync(oldCommand);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, staleReplay.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationOutcome.Committed, staleReplay.Proof?.Outcome);
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, staleReplay.Primary?.Status);
        Assert.Equal(3, staleReplay.Primary?.LifecycleVersion);
        Assert.Null(staleReplay.DeliveryOpportunity);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Remind, HumanInputRequestLifecycleStatus.Pending)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleStatus.Cancelled)]
    public async Task Supersede_replay_suppresses_delivery_after_related_head_advances(
        HumanInputRequestLifecycleOperationKind followUpKind,
        HumanInputRequestLifecycleStatus expectedRelatedStatus)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
        var replacement = HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(original);
        var supersede = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Supersede,
            "supersede-before-related-advance",
            original.RequestId,
            replacement);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(supersede)).Status);
        var freshReplay = await harness.Service.MutateAsync(supersede);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, freshReplay.Status);
        Assert.NotNull(freshReplay.DeliveryOpportunity);
        var followUp = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            followUpKind,
            $"related-{followUpKind.ToString().ToLowerInvariant()}-after-supersede",
            replacement.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(followUp)).Status);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        RejectMutableDependencies(harness);

        var staleReplay = await harness.Service.MutateAsync(supersede);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, staleReplay.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Superseded, staleReplay.Primary?.Status);
        Assert.Equal(expectedRelatedStatus, staleReplay.Related?.Status);
        Assert.Equal(2, staleReplay.Related?.LifecycleVersion);
        Assert.Null(staleReplay.DeliveryOpportunity);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    private static HumanInputRequest? Candidate(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequest original)
        => kind switch
        {
            HumanInputRequestLifecycleOperationKind.Reroute =>
                HumanInputRequestLifecycleTransitionTestSupport.RerouteCandidate(original),
            HumanInputRequestLifecycleOperationKind.Amend =>
                HumanInputRequestLifecycleTransitionTestSupport.AmendCandidate(original),
            _ => null,
        };

    private static void RejectMutableDependencies(HumanInputRequestLifecycleHarness harness)
    {
        harness.Resolver.Handler = (_, _) => throw new InvalidOperationException("Replay must not resolve mutable grant state.");
        harness.Authorizer.Handler = (_, _) => throw new InvalidOperationException("Replay must not resolve mutable actor state.");
    }
}

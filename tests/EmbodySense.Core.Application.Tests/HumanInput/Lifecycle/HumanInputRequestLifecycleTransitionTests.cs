using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleTransitionTests
{
    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Remind)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reroute)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Amend)]
    public async Task Delivery_producing_transitions_commit_exact_versions_without_related_reads(
        HumanInputRequestLifecycleOperationKind kind)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var candidate = kind switch
        {
            HumanInputRequestLifecycleOperationKind.Reroute => HumanInputRequestLifecycleTransitionTestSupport.RerouteCandidate(original),
            HumanInputRequestLifecycleOperationKind.Amend => HumanInputRequestLifecycleTransitionTestSupport.AmendCandidate(original),
            _ => null,
        };
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            kind,
            $"{kind.ToString().ToLowerInvariant()}-request-one",
            original.RequestId,
            candidate);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, result.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, result.Primary?.Status);
        Assert.Equal(2, result.Primary?.LifecycleVersion);
        Assert.Equal(kind == HumanInputRequestLifecycleOperationKind.Remind ? 1 : 0, result.Primary?.ReminderCount);
        Assert.Equal(candidate?.RequestHash ?? original.RequestHash, result.Primary?.CurrentRequest.RequestHash);
        Assert.Equal(candidate?.RequestHash ?? original.RequestHash, result.DeliveryOpportunity?.Request.RequestHash);
        Assert.Null(result.Related);
        Assert.Single(harness.Resolver.Calls);
        Assert.Single(harness.Authorizer.Requests);
        Assert.Null(Assert.Single(harness.Store.MutationReads).RelatedRequestId);
        Assert.Null(Assert.Single(harness.Store.Commits).Mutation.Operation.RelatedRequestId);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reject, HumanInputRequestLifecycleStatus.Rejected)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Cancel, HumanInputRequestLifecycleStatus.Cancelled)]
    public async Task Cleanup_transitions_use_trusted_clock_and_never_resolve_a_grant(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequestLifecycleStatus expectedStatus)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            kind,
            $"{kind.ToString().ToLowerInvariant()}-request-one",
            original.RequestId);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, result.Status);
        Assert.Equal(expectedStatus, result.Primary?.Status);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Single(harness.Authorizer.Requests);
        Assert.Equal(1, harness.Time.Calls);
        Assert.Null(Assert.Single(harness.Store.MutationReads).RelatedRequestId);
        var evidence = Assert.Single(harness.Store.Commits).Mutation.Operation;
        Assert.Null(evidence.GrantReference);
        Assert.Null(evidence.GrantDependencyEvidenceHash);
    }

    [Fact]
    public async Task Expire_conflicts_at_inclusive_endpoint_and_commits_strictly_after_it()
    {
        var endpointHarness = new HumanInputRequestLifecycleHarness();
        var endpointRequest = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(endpointHarness, endpointRequest);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(endpointHarness);
        endpointHarness.Time.Value = endpointRequest.Timing.ExpiresAtUtc;
        var endpointCommand = HumanInputRequestLifecycleTransitionTestSupport.Command(
            endpointHarness,
            HumanInputRequestLifecycleOperationKind.Expire,
            "expire-at-endpoint",
            endpointRequest.RequestId);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            endpointHarness,
            endpointCommand,
            HumanInputRequestLifecycleMutationStatus.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict);
        Assert.Empty(endpointHarness.Resolver.Calls);
        Assert.Equal(1, endpointHarness.Time.Calls);

        var afterHarness = new HumanInputRequestLifecycleHarness();
        var afterRequest = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(afterHarness, afterRequest);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(afterHarness);
        afterHarness.Time.Value = afterRequest.Timing.ExpiresAtUtc.AddTicks(1);
        var afterCommand = HumanInputRequestLifecycleTransitionTestSupport.Command(
            afterHarness,
            HumanInputRequestLifecycleOperationKind.Expire,
            "expire-after-endpoint",
            afterRequest.RequestId);

        var committed = await afterHarness.Service.MutateAsync(afterCommand);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, committed.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Expired, committed.Primary?.Status);
        Assert.Null(committed.DeliveryOpportunity);
        Assert.Empty(afterHarness.Resolver.Calls);
        Assert.Equal(1, afterHarness.Time.Calls);
        Assert.Null(Assert.Single(afterHarness.Store.MutationReads).RelatedRequestId);
    }

    [Fact]
    public async Task Supersede_atomically_projects_both_lifecycles_and_delivers_only_the_replacement()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var replacement = HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(original);
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Supersede,
            "supersede-request-one",
            original.RequestId,
            replacement);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, result.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Superseded, result.Primary?.Status);
        Assert.Equal(replacement.RequestId, result.Primary?.SupersededByRequestId);
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, result.Related?.Status);
        Assert.Equal(original.RequestId, result.Related?.SupersedesRequestId);
        Assert.Equal(replacement.RequestHash, result.Related?.CurrentRequest.RequestHash);
        Assert.Equal(replacement.RequestHash, result.DeliveryOpportunity?.Request.RequestHash);
        Assert.Equal(replacement.RequestId, Assert.Single(harness.Store.MutationReads).RelatedRequestId);
        var mutation = Assert.Single(harness.Store.Commits).Mutation;
        Assert.NotNull(mutation.PrimaryHeadToWrite);
        Assert.NotNull(mutation.SecondaryHeadToWrite);
        Assert.Equal(replacement.RequestId, mutation.Operation.RelatedRequestId);
        Assert.Equal(mutation.Operation, Assert.Single(harness.Store.Snapshot(original.RequestId)!.Operations.Skip(1)));
        Assert.Equal(mutation.Operation, Assert.Single(harness.Store.Snapshot(replacement.RequestId)!.Operations));

        var replay = await harness.Service.MutateAsync(command);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(replacement.RequestId, replay.Related?.RequestId);
        Assert.NotNull(replay.DeliveryOpportunity);
        Assert.Single(harness.Store.Commits);
    }
}

using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleReplayAuthorizationTests
{
    [Fact]
    public async Task Exact_replay_requires_current_authorization_and_the_durable_actor()
    {
        var (harness, command) = await CreateCancelledRequestAsync("replay-current-authorization");
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);

        var replay = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, replay.Status);
        Assert.NotNull(replay.Proof);
        Assert.Single(harness.Authorizer.Requests);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Store.Commits);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleActorAuthorizationStatus.Denied)]
    [InlineData(HumanInputRequestLifecycleActorAuthorizationStatus.Unavailable)]
    public async Task Exact_replay_fails_closed_when_current_authorization_is_not_authorized(
        HumanInputRequestLifecycleActorAuthorizationStatus authorizationStatus)
    {
        var (harness, command) = await CreateCancelledRequestAsync($"replay-{authorizationStatus.ToString().ToLowerInvariant()}");
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Authorizer.Handler = (request, _) => Authorization(request, authorizationStatus, null);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(
            authorizationStatus == HumanInputRequestLifecycleActorAuthorizationStatus.Denied
                ? HumanInputRequestLifecycleMutationStatus.Denied
                : HumanInputRequestLifecycleMutationStatus.Unavailable,
            result.Status);
        Assert.Null(result.Proof);
        Assert.Single(harness.Authorizer.Requests);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Exact_replay_denies_a_different_current_actor()
    {
        var (harness, command) = await CreateCancelledRequestAsync("replay-different-actor");
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Authorizer.Handler = (request, _) => Authorization(
            request,
            HumanInputRequestLifecycleActorAuthorizationStatus.Authorized,
            AuthorityGrantApplicationTestFixture.Actor("different-human-input-actor"));

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Denied, result.Status);
        Assert.Null(result.Proof);
        Assert.Single(harness.Authorizer.Requests);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Store_replayed_commit_requires_the_established_actor_to_match_durable_evidence()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request("store-replayed-request", "store-replayed-version");
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request, "seed-store-replayed-request");
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.CommitOverride = (mutation, _) => Task.FromResult(harness.Store.CommitDurably(mutation) with
        {
            Status = HumanInputRequestLifecycleStoreCommitStatus.Replayed,
        });
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            "store-replayed-operation",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, result.Status);
        Assert.NotNull(result.Proof);
        Assert.Single(harness.Authorizer.Requests);
        Assert.Single(harness.Store.Commits);
    }

    [Fact]
    public async Task Exact_operation_conflict_recovers_a_replay_for_the_established_actor()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request("operation-conflict-request", "operation-conflict-version");
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request, "seed-operation-conflict-request");
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.CommitOverride = (mutation, _) => Task.FromResult(harness.Store.CommitDurably(mutation) with
        {
            Status = HumanInputRequestLifecycleStoreCommitStatus.OperationConflict,
        });
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            "operation-conflict-replay",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, result.Status);
        Assert.NotNull(result.Proof);
        Assert.Single(harness.Authorizer.Requests);
        Assert.Single(harness.Store.Commits);
        Assert.Equal(2, harness.Store.MutationReads.Count);
    }

    private static async Task<(HumanInputRequestLifecycleHarness Harness, HumanInputRequestLifecycleCommand Command)> CreateCancelledRequestAsync(
        string requestId)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request(requestId, $"{requestId}-version");
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request, $"seed-{requestId}");
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            $"cancel-{requestId}",
            request.RequestId);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(command)).Status);
        return (harness, command);
    }

    private static HumanInputRequestLifecycleActorAuthorization Authorization(
        HumanInputRequestLifecycleActorAuthorizationRequest request,
        HumanInputRequestLifecycleActorAuthorizationStatus status,
        AuthorityActorId? actorId)
        => new(
            status,
            request.Command.OperationId,
            request.RequestHash,
            request.WorkspaceId,
            request.EvaluatedAtUtc,
            status == HumanInputRequestLifecycleActorAuthorizationStatus.Unavailable
                ? null
                : actorId ?? AuthorityGrantApplicationTestFixture.Actor("human-input-actor"),
            status == HumanInputRequestLifecycleActorAuthorizationStatus.Unavailable
                ? string.Empty
                : HumanInputRequestLifecycleTestData.Hash('a'));
}

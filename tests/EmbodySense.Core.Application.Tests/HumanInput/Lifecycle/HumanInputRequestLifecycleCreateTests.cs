using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleCreateTests
{
    [Fact]
    public async Task Create_commits_exact_server_owned_authority_evidence_before_delivery_is_exposed()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        var command = CreateCommand(harness, request);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, result.Status);
        Assert.Equal(command.OperationId, result.OperationId);
        Assert.Equal(command.RequestHash, result.RequestHash);
        Assert.Equal(HumanInputRequestLifecycleOperationOutcome.Committed, result.Proof?.Outcome);
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, result.Primary?.Status);
        Assert.Equal(request.RequestHash, result.Primary?.CurrentRequest.RequestHash);
        Assert.Equal(command.OperationId, result.DeliveryOpportunity?.OperationId);
        Assert.Equal(request.RequestHash, result.DeliveryOpportunity?.Request.RequestHash);
        Assert.Equal(HumanInputRequestLifecycleTestData.Now, result.DeliveryOpportunity?.ProvedAtUtc);

        var resolutionCall = Assert.Single(harness.Resolver.Calls);
        Assert.Equal(command.GrantReference, resolutionCall.Reference);
        var authorization = Assert.Single(harness.Authorizer.Requests);
        Assert.NotSame(command, authorization.Command);
        Assert.Equal(command.OperationId, authorization.Command.OperationId);
        Assert.Equal(command.RequestHash, authorization.Command.RequestHash);
        Assert.Equal(command.CandidateRequest?.RequestHash, authorization.Command.CandidateRequest?.RequestHash);
        Assert.NotSame(command.CandidateRequest?.EligibleRespondents, authorization.Command.CandidateRequest?.EligibleRespondents);
        Assert.Equal(command.RequestHash, authorization.RequestHash);
        Assert.Equal("workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", authorization.WorkspaceId);
        Assert.Equal(HumanInputRequestLifecycleTestData.Now, authorization.EvaluatedAtUtc);
        Assert.Equal(0, harness.Time.Calls);

        var commit = Assert.Single(harness.Store.Commits);
        Assert.Equal(CancellationToken.None, commit.CancellationToken);
        Assert.Equal("human-input-actor", commit.Mutation.Operation.ActorId.Value);
        Assert.Equal(HumanInputRequestLifecycleTestData.Hash('a'), commit.Mutation.Operation.AuthorityEvidenceHash);
        Assert.Equal(HumanInputRequestLifecycleTestData.Hash('d'), commit.Mutation.Operation.GrantDependencyEvidenceHash);
        Assert.Equal(command.GrantReference, commit.Mutation.Operation.GrantReference);
        Assert.Equal(HumanInputRequestLifecycleTestData.Now, commit.Mutation.Operation.RecordedAtUtc);
        Assert.NotNull(harness.Store.Snapshot(request.RequestId));
    }

    [Fact]
    public async Task Exact_replay_returns_durable_proof_before_clock_actor_or_grant_dependencies()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        var command = CreateCommand(harness, request);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(command)).Status);
        harness.Resolver.Calls.Clear();
        harness.Authorizer.Requests.Clear();
        harness.Resolver.Handler = (_, _) => throw new InvalidOperationException("Grant must not be resolved.");
        harness.Authorizer.Handler = (_, _) => throw new InvalidOperationException("Actor must not be resolved.");
        harness.Time.ThrowOnRead = true;

        var replay = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, replay.Status);
        Assert.NotNull(replay.Proof);
        Assert.NotNull(replay.DeliveryOpportunity);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Equal(0, harness.Time.Calls);
        Assert.Single(harness.Store.Commits);
    }

    [Fact]
    public async Task Reused_operation_id_with_changed_intent_conflicts_before_dependencies()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        var original = CreateCommand(harness, request);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(original)).Status);
        harness.Resolver.Calls.Clear();
        harness.Authorizer.Requests.Clear();
        var changed = HumanInputRequestLifecycleCommandHash.Apply(original with
        {
            Reason = EmbodySense.Core.Application.Tests.Governance.Authority.Grants.AuthorityGrantApplicationTestFixture.Purpose(
                "Changed bounded lifecycle intent."),
        });

        var result = await harness.Service.MutateAsync(changed);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Single(harness.Store.Commits);
    }

    [Fact]
    public async Task Caller_owned_candidate_is_deeply_snapshotted_before_the_first_async_boundary()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        var command = CreateCommand(harness, request);
        harness.Store.ReadForMutationOverride = (_, _, _, _, _) =>
        {
            request.EligibleRespondents[0] = request.EligibleRespondents[0] with
            {
                RoutingReference = "tampered-after-invocation",
            };
            harness.Store.ReadForMutationOverride = null;
            return Task.FromResult(new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.NotFound,
                0,
                null,
                null,
                null));
        };

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, result.Status);
        Assert.Equal("tampered-after-invocation", request.EligibleRespondents[0].RoutingReference);
        var mutation = Assert.Single(harness.Store.Commits).Mutation;
        Assert.Equal("private-route-one", mutation.RequestToAppend?.EligibleRespondents[0].RoutingReference);
        Assert.NotSame(request.EligibleRespondents, mutation.RequestToAppend?.EligibleRespondents);
    }

    private static HumanInputRequestLifecycleCommand CreateCommand(
        HumanInputRequestLifecycleHarness harness,
        EmbodySense.Core.Common.HumanInput.Models.HumanInputRequest request)
        => HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "create-request-one",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            request);
}

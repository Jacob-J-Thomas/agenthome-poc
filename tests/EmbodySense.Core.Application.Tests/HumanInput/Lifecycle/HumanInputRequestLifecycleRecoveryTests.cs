using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleRecoveryTests
{
    [Fact]
    public async Task Store_conflict_retries_exactly_twice_with_fresh_authority_per_attempt()
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.CommitOverride = (mutation, _) => Task.FromResult(
            new HumanInputRequestLifecycleStoreCommitResult(
                HumanInputRequestLifecycleStoreCommitStatus.StoreConflict,
                mutation.ExpectedStoreGeneration + 1,
                null,
                null,
                null));
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "retry-store-conflict-twice",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, result.Status);
        Assert.Null(result.Proof);
        Assert.Equal(2, harness.Store.MutationReads.Count);
        Assert.Equal(2, harness.Store.Commits.Count);
        Assert.Equal(2, harness.Resolver.Calls.Count);
        Assert.Equal(2, harness.Authorizer.Requests.Count);
        Assert.All(harness.Store.Commits, commit => Assert.Equal(CancellationToken.None, commit.CancellationToken));
    }

    [Fact]
    public async Task One_store_conflict_replans_and_commits_on_second_attempt()
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var attempts = 0;
        harness.Store.CommitOverride = (mutation, _) =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? new HumanInputRequestLifecycleStoreCommitResult(
                    HumanInputRequestLifecycleStoreCommitStatus.StoreConflict,
                    mutation.ExpectedStoreGeneration + 1,
                    null,
                    null,
                    null)
                : harness.Store.CommitDurably(mutation));
        };
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "retry-then-commit",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, result.Status);
        Assert.NotNull(result.Proof);
        Assert.NotNull(result.DeliveryOpportunity);
        Assert.Equal(2, attempts);
        Assert.Equal(2, harness.Store.MutationReads.Count);
        Assert.Equal(2, harness.Store.Commits.Count);
        Assert.Equal(2, harness.Resolver.Calls.Count);
        Assert.Equal(2, harness.Authorizer.Requests.Count);
    }

    [Fact]
    public async Task Store_conflict_refreshes_dependency_time_and_authority_before_persisting_replanned_conflict()
    {
        var firstEvaluation = HumanInputRequestLifecycleTestData.Now.AddMinutes(1);
        var secondEvaluation = HumanInputRequestLifecycleTestData.Now.AddMinutes(2);
        var (harness, request) = await SeededHarnessAsync();
        var loser = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "replanned-loser-with-fresh-authority",
            request.RequestId);

        var competitorHarness = new HumanInputRequestLifecycleHarness();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(competitorHarness, request);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(competitorHarness);
        competitorHarness.Resolver.Handler = (_, _) =>
            HumanInputRequestLifecycleTestData.ActiveResolution(competitorHarness.Grant, secondEvaluation);
        var competitor = HumanInputRequestLifecycleTransitionTestSupport.Command(
            competitorHarness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "competing-reminder-at-fresh-time",
            request.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await competitorHarness.Service.MutateAsync(competitor)).Status);
        var competingMutation = Assert.Single(competitorHarness.Store.Commits).Mutation;

        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Resolver.Handler = (_, _) => HumanInputRequestLifecycleTestData.ActiveResolution(
            harness.Grant,
            harness.Resolver.Calls.Count == 1 ? firstEvaluation : secondEvaluation);
        var attempts = 0;
        harness.Store.CommitOverride = (mutation, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                var competingCommit = harness.Store.CommitDurably(competingMutation);
                Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, competingCommit.Status);
                return Task.FromResult(new HumanInputRequestLifecycleStoreCommitResult(
                    HumanInputRequestLifecycleStoreCommitStatus.StoreConflict,
                    competingCommit.StoreGeneration,
                    null,
                    null,
                    null));
            }

            return Task.FromResult(harness.Store.CommitDurably(mutation));
        };

        var result = await harness.Service.MutateAsync(loser);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, result.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict, result.Proof?.FailureCode);
        Assert.Equal(2, harness.Resolver.Calls.Count);
        Assert.Equal(2, harness.Authorizer.Requests.Count);
        Assert.Collection(
            harness.Authorizer.Requests,
            request => Assert.Equal(firstEvaluation, request.EvaluatedAtUtc),
            request => Assert.Equal(secondEvaluation, request.EvaluatedAtUtc));
        Assert.Equal(2, harness.Store.Commits.Count);
        Assert.Equal(secondEvaluation, harness.Store.Commits[1].Mutation.Operation.RecordedAtUtc);

        var replay = await harness.Service.MutateAsync(loser);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict, replay.Proof?.FailureCode);
        Assert.Equal(2, harness.Resolver.Calls.Count);
        Assert.Equal(2, harness.Authorizer.Requests.Count);
        Assert.Equal(2, harness.Store.Commits.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Post_intent_throw_ambiguous_or_malformed_commit_recovers_exact_durable_operation(int scenario)
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.CommitOverride = (mutation, _) =>
        {
            var durable = harness.Store.CommitDurably(mutation);
            return scenario switch
            {
                0 => throw new InvalidOperationException("Commit acknowledgement was lost."),
                1 => Task.FromResult(durable with { Status = HumanInputRequestLifecycleStoreCommitStatus.Ambiguous }),
                2 => Task.FromResult<HumanInputRequestLifecycleStoreCommitResult>(null!),
                _ => Task.FromResult(durable with
                {
                    Status = HumanInputRequestLifecycleStoreCommitStatus.Committed,
                    StoredOperation = null,
                }),
            };
        };
        using var cancellation = new CancellationTokenSource();
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            $"recover-post-intent-{scenario}",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command, cancellation.Token);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, result.Status);
        Assert.NotNull(result.Proof);
        Assert.NotNull(result.DeliveryOpportunity);
        Assert.Single(harness.Store.Commits);
        Assert.Equal(CancellationToken.None, harness.Store.Commits[0].CancellationToken);
        Assert.Equal(2, harness.Store.MutationReads.Count);
        Assert.Equal(cancellation.Token, harness.Store.MutationReads[0].CancellationToken);
        Assert.Equal(CancellationToken.None, harness.Store.MutationReads[1].CancellationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Pre_commit_failure_without_durable_evidence_returns_ambiguous_without_delivery(int scenario)
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.CommitOverride = (mutation, _) => scenario switch
        {
            0 => throw new InvalidOperationException("Commit failed before persistence."),
            1 => Task.FromResult<HumanInputRequestLifecycleStoreCommitResult>(null!),
            _ => Task.FromResult(new HumanInputRequestLifecycleStoreCommitResult(
                HumanInputRequestLifecycleStoreCommitStatus.Ambiguous,
                mutation.ExpectedStoreGeneration,
                null,
                null,
                null)),
        };
        using var cancellation = new CancellationTokenSource();
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            $"ambiguous-before-persistence-{scenario}",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command, cancellation.Token);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Single(harness.Store.Commits);
        Assert.Equal(2, harness.Store.MutationReads.Count);
        Assert.Equal(CancellationToken.None, harness.Store.MutationReads[1].CancellationToken);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleStoreCommitStatus.OperationConflict, HumanInputRequestLifecycleMutationStatus.Conflict)]
    [InlineData(HumanInputRequestLifecycleStoreCommitStatus.LimitExceeded, HumanInputRequestLifecycleMutationStatus.LimitExceeded)]
    [InlineData(HumanInputRequestLifecycleStoreCommitStatus.Unavailable, HumanInputRequestLifecycleMutationStatus.Unavailable)]
    public async Task Closed_store_dispositions_map_without_forging_durable_proof(
        HumanInputRequestLifecycleStoreCommitStatus storeStatus,
        HumanInputRequestLifecycleMutationStatus expectedStatus)
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.CommitOverride = (mutation, _) => Task.FromResult(
            new HumanInputRequestLifecycleStoreCommitResult(
                storeStatus,
                mutation.ExpectedStoreGeneration,
                null,
                null,
                null));
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            $"closed-store-{storeStatus.ToString().ToLowerInvariant()}",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Single(harness.Store.Commits);
        Assert.Single(harness.Store.MutationReads);
    }

    [Fact]
    public async Task Cancellation_after_authorization_but_before_durable_intent_never_calls_commit()
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        using var cancellation = new CancellationTokenSource();
        harness.Authorizer.Handler = (authorizationRequest, _) =>
        {
            cancellation.Cancel();
            return new HumanInputRequestLifecycleActorAuthorization(
                HumanInputRequestLifecycleActorAuthorizationStatus.Authorized,
                authorizationRequest.Command.OperationId,
                authorizationRequest.RequestHash,
                authorizationRequest.WorkspaceId,
                authorizationRequest.EvaluatedAtUtc,
                AuthorityGrantApplicationTestFixture.Actor("human-input-actor"),
                HumanInputRequestLifecycleTestData.Hash('a'));
        };
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "cancel-before-durable-intent",
            request.RequestId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Service.MutateAsync(command, cancellation.Token));

        Assert.Single(harness.Resolver.Calls);
        Assert.Single(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
        Assert.Single(harness.Store.MutationReads);
    }

    [Fact]
    public async Task Committed_claim_without_valid_durable_proof_never_exposes_delivery()
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.CommitOverride = (mutation, _) => Task.FromResult(
            new HumanInputRequestLifecycleStoreCommitResult(
                HumanInputRequestLifecycleStoreCommitStatus.Committed,
                mutation.ExpectedStoreGeneration + 1,
                null,
                null,
                null));
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "commit-without-proof",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.DeliveryOpportunity);
    }

    private static async Task<(HumanInputRequestLifecycleHarness Harness, EmbodySense.Core.Common.HumanInput.Models.HumanInputRequest Request)> SeededHarnessAsync()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        return (harness, request);
    }
}

using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleBlockerRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_committed_supersede_halves_reject_cross_owned_artifact_corruption(
        bool inspectRelatedHalf)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        var replacement = HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(original);
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
        var supersede = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Supersede,
            "supersede-before-hostile-history",
            original.RequestId,
            replacement);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(supersede)).Status);
        var primary = harness.Store.Snapshot(original.RequestId)!;
        var related = harness.Store.Snapshot(replacement.RequestId)!;
        var evidence = primary.Operations[^1];
        HumanInputRequestLifecycleStoreSnapshot hostile;
        string targetId;
        HumanInputRequestLifecycleHead expected;
        if (inspectRelatedHalf)
        {
            var forgedPrevious = evidence.PreviousHead! with
            {
                CurrentRequest = new HumanInputRequestReference(
                    1,
                    original.RequestId,
                    "forged-original-version",
                    HumanInputRequestLifecycleTestData.Hash('e')),
            };
            hostile = new HumanInputRequestLifecycleStoreSnapshot(
                related.Head,
                related.RequestVersions,
                [evidence with { PreviousHead = forgedPrevious }]);
            targetId = replacement.RequestId;
            expected = related.Head;
        }
        else
        {
            var forgedCandidate = new HumanInputRequestReference(
                1,
                replacement.RequestId,
                "forged-replacement-version",
                HumanInputRequestLifecycleTestData.Hash('e'));
            hostile = new HumanInputRequestLifecycleStoreSnapshot(
                primary.Head,
                primary.RequestVersions,
                [primary.Operations[0], evidence with { CandidateRequest = forgedCandidate }]);
            targetId = original.RequestId;
            expected = primary.Head with { Status = HumanInputRequestLifecycleStatus.Pending };
        }

        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.ReadForMutationOverride = (_, _, _, _, _) => Task.FromResult(
            new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                10,
                hostile,
                null,
                null));
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            inspectRelatedHalf ? "inspect-hostile-related-half" : "inspect-hostile-primary-half",
            targetId,
            expected: expected);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.Primary);
        Assert.Null(result.Related);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Noncommitted_supersede_replay_requires_consistent_related_proof(bool returnStaleRelated)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        var replacement = HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(original);
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original, "seed-primary-for-proof");
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, replacement, "seed-related-for-proof");
        var relatedBeforeReceipt = harness.Store.Snapshot(replacement.RequestId)!;
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Supersede,
            "supersede-existing-for-proof",
            original.RequestId,
            replacement);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Conflict,
            (await harness.Service.MutateAsync(command)).Status);
        var primary = harness.Store.Snapshot(original.RequestId)!;
        var evidence = primary.Operations[^1];
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.ReadForMutationOverride = (_, _, _, _, _) => Task.FromResult(
            new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                3,
                primary,
                returnStaleRelated ? relatedBeforeReceipt : null,
                new HumanInputRequestLifecycleStoredOperation(original.RequestId, evidence)));

        var replay = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Ambiguous, replay.Status);
        Assert.Null(replay.Proof);
        Assert.Null(replay.Primary);
        Assert.Null(replay.Related);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Wrong_loop_grant_cannot_persist_or_reveal_create_existing_receipt()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        var wrongRevision = GovernedLoopRevisionReference.Create(
            1,
            "different-loop",
            "different-revision",
            HumanInputRequestLifecycleTestData.Hash('6'));
        var wrongPin = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            wrongRevision,
            "publish-different-loop",
            HumanInputRequestLifecycleTestData.Hash('7'));
        var wrongGrant = AuthorityGrantApplicationTestFixture.Grant(
            binding: AuthorityGrantApplicationTestFixture.Binding() with { Loop = wrongPin },
            recordedAtUtc: HumanInputRequestLifecycleTestData.Now.AddMinutes(-10));
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Resolver.Handler = (_, _) => HumanInputRequestLifecycleTestData.ActiveResolution(wrongGrant);
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "wrong-loop-create-existing",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(wrongGrant),
            request);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.GrantUnavailable, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.Primary);
        Assert.Null(result.Related);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Single(harness.Resolver.Calls);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Cross_graph_same_revision_grant_cannot_persist_deliver_or_project()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        var crossGraphRevision = GovernedLoopRevisionReference.Create(
            1,
            "different-loop",
            request.Binding.LoopRevisionId,
            HumanInputRequestLifecycleTestData.Hash('8'));
        var crossGraphPin = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            crossGraphRevision,
            "publish-cross-graph-loop",
            HumanInputRequestLifecycleTestData.Hash('9'));
        var crossGraphGrant = AuthorityGrantApplicationTestFixture.Grant(
            binding: AuthorityGrantApplicationTestFixture.Binding() with { Loop = crossGraphPin },
            recordedAtUtc: HumanInputRequestLifecycleTestData.Now.AddMinutes(-10));
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Resolver.Handler = (_, _) => HumanInputRequestLifecycleTestData.ActiveResolution(crossGraphGrant);
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "cross-graph-create-existing",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(crossGraphGrant),
            request);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.GrantUnavailable, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.Primary);
        Assert.Null(result.Related);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Single(harness.Resolver.Calls);
        Assert.Empty(harness.Store.Commits);
    }
}

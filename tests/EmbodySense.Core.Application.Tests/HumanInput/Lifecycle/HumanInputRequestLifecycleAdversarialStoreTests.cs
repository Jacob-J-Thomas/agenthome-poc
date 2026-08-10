using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleAdversarialStoreTests
{
    [Fact]
    public async Task Malformed_read_shapes_snapshots_and_operation_proofs_fail_before_dependencies()
    {
        for (var scenario = 0; scenario < 14; scenario++)
        {
            var harness = new HumanInputRequestLifecycleHarness();
            var request = HumanInputRequestLifecycleTestData.Request();
            await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
            var valid = harness.Store.Snapshot(request.RequestId)!;
            HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
            var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
                harness,
                HumanInputRequestLifecycleOperationKind.Remind,
                $"hostile-read-{scenario}",
                request.RequestId);
            harness.Store.ReadForMutationOverride = (_, _, _, _, _) => Task.FromResult(
                HostileRead(scenario, request, valid, command));

            var result = await harness.Service.MutateAsync(command);

            Assert.Equal(HumanInputRequestLifecycleMutationStatus.Ambiguous, result.Status);
            Assert.Null(result.Proof);
            Assert.Null(result.Primary);
            Assert.Null(result.Related);
            Assert.Null(result.DeliveryOpportunity);
            Assert.Empty(harness.Resolver.Calls);
            Assert.Empty(harness.Authorizer.Requests);
            Assert.Empty(harness.Store.Commits);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Corrupt_committed_supersede_proof_never_exposes_delivery(int scenario)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var replacement = HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(original);
        harness.Store.CommitOverride = (mutation, _) => Task.FromResult(
            CorruptSupersedeCommit(scenario, harness, mutation));
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Supersede,
            $"corrupt-supersede-proof-{scenario}",
            original.RequestId,
            replacement);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Single(harness.Store.Commits);
        Assert.Equal(2, harness.Store.MutationReads.Count);
    }

    [Fact]
    public async Task Duplicate_request_version_in_committed_proof_is_rejected_and_recovered_fail_closed()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var candidate = HumanInputRequestLifecycleTransitionTestSupport.RerouteCandidate(original);
        harness.Store.CommitOverride = (mutation, _) =>
        {
            var previous = harness.Store.Snapshot(original.RequestId)!;
            var duplicate = HumanInputRequestHash.Apply(candidate with
            {
                Prompt = "Different private content under a duplicate version identity.",
                RequestHash = string.Empty,
            });
            var snapshot = new HumanInputRequestLifecycleStoreSnapshot(
                mutation.PrimaryHeadToWrite!,
                previous.RequestVersions.Concat([candidate, duplicate]).ToArray(),
                previous.Operations.Append(mutation.Operation).ToArray());
            return Task.FromResult(new HumanInputRequestLifecycleStoreCommitResult(
                HumanInputRequestLifecycleStoreCommitStatus.Committed,
                mutation.ExpectedStoreGeneration + 1,
                new HumanInputRequestLifecycleStoredOperation(original.RequestId, mutation.Operation),
                snapshot,
                null));
        };
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Reroute,
            "duplicate-version-proof",
            original.RequestId,
            candidate);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.DeliveryOpportunity);
    }

    [Fact]
    public async Task Workspace_global_operation_collision_across_primary_and_related_snapshots_fails_before_dependencies()
    {
        const string CollidingOperationId = "cross-stream-create-collision";
        var primaryHarness = new HumanInputRequestLifecycleHarness();
        var primaryRequest = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(
            primaryHarness,
            primaryRequest,
            CollidingOperationId);
        var primary = primaryHarness.Store.Snapshot(primaryRequest.RequestId)!;

        var relatedHarness = new HumanInputRequestLifecycleHarness();
        var relatedRequest = HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(primaryRequest);
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(
            relatedHarness,
            relatedRequest,
            CollidingOperationId);
        var related = relatedHarness.Store.Snapshot(relatedRequest.RequestId)!;

        Assert.Equal(primary.Operations[0].OperationId, related.Operations[0].OperationId);
        Assert.NotEqual(primary.Operations[0].RequestHash, related.Operations[0].RequestHash);

        var harness = new HumanInputRequestLifecycleHarness();
        harness.Store.ReadForMutationOverride = (_, _, _, _, _) => Task.FromResult(
            new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                2,
                primary,
                related,
                null));
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Supersede,
            "inspect-cross-stream-create-collision",
            primaryRequest.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            relatedRequest,
            primary.Head,
            primaryRequest.Binding);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.Primary);
        Assert.Null(result.Related);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    private static HumanInputRequestLifecycleStoreReadResult HostileRead(
        int scenario,
        EmbodySense.Core.Common.HumanInput.Models.HumanInputRequest request,
        HumanInputRequestLifecycleStoreSnapshot valid,
        HumanInputRequestLifecycleCommand command)
    {
        var duplicateVersion = HumanInputRequestHash.Apply(request with
        {
            Prompt = "Different private content under the same version identity.",
            RequestHash = string.Empty,
        });
        var duplicateVersionSnapshot = new HumanInputRequestLifecycleStoreSnapshot(
            valid.Head,
            [request, duplicateVersion],
            valid.Operations);
        var duplicateOperationSnapshot = new HumanInputRequestLifecycleStoreSnapshot(
            valid.Head,
            valid.RequestVersions,
            [valid.Operations[0], valid.Operations[0]]);
        var excessiveOperationSnapshot = new HumanInputRequestLifecycleStoreSnapshot(
            valid.Head,
            valid.RequestVersions,
            Enumerable.Repeat(valid.Operations[0], HumanInputRequestLifecycleContractLimits.MaxOperationsPerRequest + 1).ToArray());
        var uncontainedEvidence = valid.Operations[0] with
        {
            OperationId = command.OperationId,
            RequestHash = command.RequestHash,
        };
        var invalidEvidence = uncontainedEvidence with { AuthorityEvidenceHash = "invalid" };
        var changedIntent = valid.Operations[0] with { OperationId = command.OperationId };
        return scenario switch
        {
            0 => null!,
            1 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Unknown,
                1,
                null,
                null,
                null),
            2 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                -1,
                valid,
                null,
                null),
            3 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                long.MaxValue,
                valid,
                null,
                null),
            4 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                1,
                null,
                null,
                null),
            5 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.NotFound,
                1,
                valid,
                null,
                null),
            6 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Unavailable,
                1,
                valid,
                null,
                null),
            7 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                1,
                valid,
                valid,
                null),
            8 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                2,
                duplicateVersionSnapshot,
                null,
                null),
            9 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                2,
                duplicateOperationSnapshot,
                null,
                null),
            10 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                HumanInputRequestLifecycleContractLimits.MaxOperationsPerRequest + 1,
                excessiveOperationSnapshot,
                null,
                null),
            11 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                2,
                valid,
                null,
                new HumanInputRequestLifecycleStoredOperation(request.RequestId, uncontainedEvidence)),
            12 => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                2,
                valid,
                null,
                new HumanInputRequestLifecycleStoredOperation(request.RequestId, invalidEvidence)),
            _ => new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                2,
                valid,
                null,
                new HumanInputRequestLifecycleStoredOperation(request.RequestId, changedIntent)),
        };
    }

    private static HumanInputRequestLifecycleStoreCommitResult CorruptSupersedeCommit(
        int scenario,
        HumanInputRequestLifecycleHarness harness,
        HumanInputRequestLifecycleStoreMutation mutation)
    {
        var previous = harness.Store.Snapshot(mutation.Operation.TargetRequestId)!;
        var primary = new HumanInputRequestLifecycleStoreSnapshot(
            mutation.PrimaryHeadToWrite!,
            previous.RequestVersions,
            previous.Operations.Append(mutation.Operation).ToArray());
        var related = new HumanInputRequestLifecycleStoreSnapshot(
            mutation.SecondaryHeadToWrite!,
            [mutation.RequestToAppend!],
            [mutation.Operation]);
        var stored = new HumanInputRequestLifecycleStoredOperation(
            mutation.Operation.TargetRequestId,
            mutation.Operation);
        return scenario switch
        {
            0 => new HumanInputRequestLifecycleStoreCommitResult(
                HumanInputRequestLifecycleStoreCommitStatus.Committed,
                mutation.ExpectedStoreGeneration + 1,
                stored,
                primary,
                null),
            1 => new HumanInputRequestLifecycleStoreCommitResult(
                HumanInputRequestLifecycleStoreCommitStatus.Committed,
                mutation.ExpectedStoreGeneration + 1,
                stored,
                primary,
                new HumanInputRequestLifecycleStoreSnapshot(
                    related.Head,
                    related.RequestVersions,
                    [])),
            2 => new HumanInputRequestLifecycleStoreCommitResult(
                HumanInputRequestLifecycleStoreCommitStatus.Committed,
                mutation.ExpectedStoreGeneration + 1,
                stored,
                new HumanInputRequestLifecycleStoreSnapshot(
                    primary.Head,
                    primary.RequestVersions,
                    previous.Operations),
                related),
            _ => new HumanInputRequestLifecycleStoreCommitResult(
                HumanInputRequestLifecycleStoreCommitStatus.Committed,
                mutation.ExpectedStoreGeneration + 1,
                stored with
                {
                    Evidence = mutation.Operation with
                    {
                        AuthorityEvidenceHash = HumanInputRequestLifecycleTestData.Hash('f'),
                    },
                },
                primary,
                related),
        };
    }
}

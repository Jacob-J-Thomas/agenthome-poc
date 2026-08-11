using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleReplayProjectionTests
{
    [Fact]
    public async Task Not_found_replay_does_not_project_a_later_same_id_lifecycle_from_another_run()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var expectedRequest = Request("not-found-replay-scope", "expected-version", "expected-run");
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Remind,
            "not-found-replay-scope-operation",
            expectedRequest.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            expected: MissingHead(expectedRequest),
            expectedBinding: expectedRequest.Binding);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.NotFound, (await harness.Service.MutateAsync(command)).Status);
        var receipt = Assert.Single(harness.Store.Commits).Mutation.Operation;

        var currentRequest = Request(expectedRequest.RequestId, "current-version", "current-run");
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, currentRequest, "create-current-scope")).Status);
        var current = harness.Store.Snapshot(currentRequest.RequestId)!;
        var currentCreate = harness.Store.Commits[^1].Mutation.Operation;
        var replaySnapshot = new HumanInputRequestLifecycleStoreSnapshot(
            current.Head,
            current.RequestVersions,
            [receipt, currentCreate]);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.ReadForMutationOverride = (_, _, _, _, _) => Task.FromResult(
            new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                2,
                replaySnapshot,
                null,
                new HumanInputRequestLifecycleStoredOperation(expectedRequest.RequestId, receipt)));

        var replay = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound, replay.Proof?.FailureCode);
        Assert.Null(replay.Primary);
        Assert.Null(replay.Related);
        Assert.Null(replay.DeliveryOpportunity);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Failed_create_replay_does_not_project_a_later_different_version_from_another_run()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var failedCandidate = Request(
            "failed-create-replay-scope",
            "failed-version",
            "failed-run",
            requestedAtUtc: HumanInputRequestLifecycleTestData.Now.AddTicks(1));
        var failedCommand = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "failed-create-replay-scope-operation",
            failedCandidate.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            failedCandidate);
        var failed = await harness.Service.MutateAsync(failedCommand);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, failed.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict, failed.Proof?.FailureCode);
        var receipt = Assert.Single(harness.Store.Commits).Mutation.Operation;

        var currentRequest = Request(failedCandidate.RequestId, "current-version", "current-run");
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, currentRequest, "create-after-failed-create")).Status);
        var current = harness.Store.Snapshot(currentRequest.RequestId)!;
        var currentCreate = harness.Store.Commits[^1].Mutation.Operation;
        var replaySnapshot = new HumanInputRequestLifecycleStoreSnapshot(
            current.Head,
            current.RequestVersions,
            [receipt, currentCreate]);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.ReadForMutationOverride = (_, _, _, _, _) => Task.FromResult(
            new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                2,
                replaySnapshot,
                null,
                new HumanInputRequestLifecycleStoredOperation(failedCandidate.RequestId, receipt)));

        var replay = await harness.Service.MutateAsync(failedCommand);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict, replay.Proof?.FailureCode);
        Assert.Null(replay.Primary);
        Assert.Null(replay.Related);
        Assert.Null(replay.DeliveryOpportunity);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Ambiguous_recovery_does_not_project_a_current_lifecycle_outside_command_scope()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var currentRequest = Request("ambiguous-recovery-scope", "current-version", "current-run");
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, currentRequest, "seed-ambiguous-recovery-scope");
        var current = harness.Store.Snapshot(currentRequest.RequestId)!;
        var expectedRequest = Request(currentRequest.RequestId, "expected-version", "expected-run");
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Remind,
            "ambiguous-recovery-scope-operation",
            expectedRequest.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            expected: MissingHead(expectedRequest),
            expectedBinding: expectedRequest.Binding);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var reads = 0;
        harness.Store.ReadForMutationOverride = (_, _, _, _, _) =>
        {
            reads++;
            return Task.FromResult(reads == 1
                ? new HumanInputRequestLifecycleStoreReadResult(HumanInputRequestLifecycleStoreReadStatus.NotFound, 1, null, null, null)
                : new HumanInputRequestLifecycleStoreReadResult(HumanInputRequestLifecycleStoreReadStatus.Ready, 1, current, null, null));
        };
        harness.Store.CommitOverride = (_, _) => throw new InvalidOperationException("Commit acknowledgement and outcome were lost.");

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.Primary);
        Assert.Null(result.Related);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Equal(2, reads);
        Assert.Single(harness.Store.Commits);
    }

    private static HumanInputRequest Request(
        string requestId,
        string requestVersionId,
        string runId,
        DateTimeOffset? requestedAtUtc = null)
    {
        var request = HumanInputRequestLifecycleTestData.Request(
            requestId: requestId,
            requestVersionId: requestVersionId,
            requestedAtUtc: requestedAtUtc);
        return HumanInputRequestHash.Apply(request with
        {
            Binding = request.Binding with { RunId = runId },
            RequestHash = string.Empty,
        });
    }

    private static HumanInputRequestLifecycleHead MissingHead(HumanInputRequest request)
        => new(
            HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion,
            request.RequestId,
            1,
            HumanInputRequestLifecycleStatus.Pending,
            HumanInputRequestLifecycleTestData.Reference(request),
            0,
            null,
            null,
            "imagined-create-operation",
            HumanInputRequestLifecycleTestData.Now);
}

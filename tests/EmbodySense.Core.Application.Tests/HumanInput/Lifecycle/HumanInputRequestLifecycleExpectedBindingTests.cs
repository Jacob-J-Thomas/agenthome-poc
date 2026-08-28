using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleExpectedBindingTests
{
    [Fact]
    public async Task Expected_binding_is_hash_bound_and_changed_intent_conflicts_before_dependencies()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "binding-hash-operation",
            request.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(command)).Status);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var changed = HumanInputRequestLifecycleCommandHash.Apply(command with
        {
            ExpectedBinding = command.ExpectedBinding! with { RunId = "changed-run" },
        });

        Assert.NotEqual(command.RequestHash, changed.RequestHash);
        Assert.False(HumanInputRequestLifecycleCommandHash.Matches(
            command with { ExpectedBinding = changed.ExpectedBinding }));
        var result = await harness.Service.MutateAsync(changed);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, result.Status);
        Assert.Null(result.Proof);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Existing_target_binding_mismatch_returns_value_free_conflict_without_receipt()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var expected = harness.Store.Snapshot(request.RequestId)!.Head;
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Remind,
            "forged-run-binding",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            expected: expected,
            expectedBinding: request.Binding with { RunId = "forged-run" });

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.Primary);
        Assert.Null(result.Related);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Empty(harness.Store.Commits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Expected_binding_outside_grant_scope_cannot_persist_or_project(int scenario)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var expectedBinding = scenario switch
        {
            0 => request.Binding with { WorkspaceId = "workspace-sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            1 => request.Binding with { LoopGraphId = "different-loop" },
            _ => request.Binding with { LoopRevisionId = "different-revision" },
        };
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Remind,
            $"forged-grant-binding-{scenario}",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            expected: harness.Store.Snapshot(request.RequestId)!.Head,
            expectedBinding: expectedBinding);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.Primary);
        Assert.Null(result.Related);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Cleanup_not_found_under_wrong_workspace_cannot_persist_or_replay_receipt()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var absent = HumanInputRequestLifecycleTestData.Request(
            requestId: "wrong-workspace-missing-request",
            requestVersionId: "wrong-workspace-missing-version",
            workspaceId: "workspace-sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var expected = new HumanInputRequestLifecycleHead(
            1,
            absent.RequestId,
            1,
            HumanInputRequestLifecycleStatus.Pending,
            HumanInputRequestLifecycleTestData.Reference(absent),
            0,
            null,
            null,
            "imagined-wrong-workspace-create",
            HumanInputRequestLifecycleTestData.Now);
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Cancel,
            "wrong-workspace-cancel-not-found",
            absent.RequestId,
            null,
            expected: expected,
            expectedBinding: absent.Binding);

        var producer = new HumanInputRequestLifecycleHarness();
        var producerRequest = HumanInputRequestLifecycleTestData.Request(
            requestId: absent.RequestId,
            requestVersionId: absent.RequestVersionId);
        var producerExpected = expected with
        {
            CurrentRequest = HumanInputRequestLifecycleTestData.Reference(producerRequest),
        };
        var producerCommand = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Cancel,
            command.OperationId,
            producerRequest.RequestId,
            null,
            expected: producerExpected,
            expectedBinding: producerRequest.Binding);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.NotFound,
            (await producer.Service.MutateAsync(producerCommand)).Status);
        var hostileExactEvidence = Assert.Single(producer.Store.Commits).Mutation.Operation with
        {
            RequestHash = command.RequestHash,
            ExpectedRequest = command.ExpectedRequest,
            ExpectedBinding = command.ExpectedBinding,
        };
        harness.Store.ReadForMutationOverride = (_, _, _, _, _) => Task.FromResult(
            new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.NotFound,
                1,
                null,
                null,
                new HumanInputRequestLifecycleStoredOperation(absent.RequestId, hostileExactEvidence)));

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.Primary);
        Assert.Null(result.Related);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
        Assert.Empty(harness.Store.MutationReads);
    }

    [Fact]
    public async Task Missing_target_receipt_is_durable_only_under_exact_grant_bound_expected_binding()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var absent = HumanInputRequestLifecycleTestData.Request(
            requestId: "grant-bound-missing-request",
            requestVersionId: "grant-bound-missing-version");
        var expected = new HumanInputRequestLifecycleHead(
            1,
            absent.RequestId,
            1,
            HumanInputRequestLifecycleStatus.Pending,
            HumanInputRequestLifecycleTestData.Reference(absent),
            0,
            null,
            null,
            "imagined-grant-bound-create",
            HumanInputRequestLifecycleTestData.Now);
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Remind,
            "grant-bound-not-found",
            absent.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            expected: expected,
            expectedBinding: absent.Binding);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            command,
            HumanInputRequestLifecycleMutationStatus.NotFound,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound);
        Assert.Equal(command.RequestHash, Assert.Single(harness.Store.Commits).Mutation.Operation.RequestHash);
        Assert.Single(harness.Resolver.Calls);
        Assert.Single(harness.Authorizer.Requests);
    }

    [Fact]
    public async Task Terminal_target_with_forged_run_binding_cannot_persist_or_reveal_terminal_receipt()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        var cancel = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            "cancel-before-forged-terminal-binding",
            request.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(cancel)).Status);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var terminal = harness.Store.Snapshot(request.RequestId)!.Head;
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Remind,
            "forged-binding-terminal-request",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            expected: terminal with { Status = HumanInputRequestLifecycleStatus.Pending },
            expectedBinding: request.Binding with { RunId = "forged-terminal-run" });

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.Primary);
        Assert.Null(result.Related);
        Assert.Null(result.DeliveryOpportunity);
        Assert.Empty(harness.Store.Commits);
    }
}

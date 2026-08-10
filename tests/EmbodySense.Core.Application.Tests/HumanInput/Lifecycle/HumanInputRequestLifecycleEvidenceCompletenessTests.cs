using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleEvidenceCompletenessTests
{
    [Fact]
    public async Task Not_found_receipt_retains_the_full_authenticated_optimistic_expectation()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var absent = HumanInputRequestLifecycleTestData.Request(
            requestId: "evidence-missing-request",
            requestVersionId: "evidence-missing-version");
        var expected = MissingHead(absent);
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Remind,
            "evidence-complete-not-found",
            absent.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            expected: expected,
            expectedBinding: absent.Binding);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.NotFound, result.Status);
        Assert.Equal(command.RequestHash, result.Proof?.RequestHash);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound, result.Proof?.FailureCode);
        var evidence = Assert.Single(harness.Store.Commits).Mutation.Operation;
        Assert.Equal(command.ExpectedLifecycleVersion, evidence.ExpectedLifecycleVersion);
        Assert.Equal(command.ExpectedLifecycleStatus, evidence.ExpectedLifecycleStatus);
        Assert.Equal(command.ExpectedRequest, evidence.ExpectedRequest);
        Assert.Equal(command.ExpectedBinding, evidence.ExpectedBinding);
        Assert.Equal(absent.Binding.WorkspaceId, evidence.ExpectedBinding?.WorkspaceId);
        Assert.Equal(absent.Binding.LoopGraphId, evidence.ExpectedBinding?.LoopGraphId);
        Assert.Equal(absent.Binding.LoopRevisionId, evidence.ExpectedBinding?.LoopRevisionId);
        Assert.Equal(absent.Binding.NodeId, evidence.ExpectedBinding?.NodeId);
        Assert.Equal(absent.Binding.RunId, evidence.ExpectedBinding?.RunId);
        Assert.Equal(absent.Binding.CheckpointId, evidence.ExpectedBinding?.CheckpointId);
        Assert.Null(evidence.PreviousHead);
        Assert.Null(evidence.ResultHead);

        var replay = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(command.RequestHash, replay.Proof?.RequestHash);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound, replay.Proof?.FailureCode);
        Assert.Single(harness.Store.Commits);
    }

    [Fact]
    public async Task Stale_conflict_retains_authenticated_expectation_separately_from_observed_heads()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        var observed = harness.Store.Snapshot(request.RequestId)!.Head;
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var staleExpected = observed with { LifecycleVersion = observed.LifecycleVersion + 1 };
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Remind,
            "evidence-complete-stale-conflict",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            expected: staleExpected,
            expectedBinding: request.Binding);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Conflict, result.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict, result.Proof?.FailureCode);
        var evidence = Assert.Single(harness.Store.Commits).Mutation.Operation;
        Assert.Equal(command.ExpectedLifecycleVersion, evidence.ExpectedLifecycleVersion);
        Assert.Equal(command.ExpectedLifecycleStatus, evidence.ExpectedLifecycleStatus);
        Assert.Equal(command.ExpectedRequest, evidence.ExpectedRequest);
        Assert.Equal(command.ExpectedBinding, evidence.ExpectedBinding);
        Assert.Equal(observed, evidence.PreviousHead);
        Assert.Equal(observed, evidence.ResultHead);
        Assert.NotEqual(evidence.ExpectedLifecycleVersion, evidence.PreviousHead?.LifecycleVersion);

        var replay = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict, replay.Proof?.FailureCode);
        Assert.Single(harness.Store.Commits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Missing_malformed_or_changed_expected_evidence_fails_closed_before_dependencies(int scenario)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var absent = HumanInputRequestLifecycleTestData.Request(
            requestId: "hostile-expected-evidence-request",
            requestVersionId: "hostile-expected-evidence-version");
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Remind,
            "hostile-expected-evidence-operation",
            absent.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            expected: MissingHead(absent),
            expectedBinding: absent.Binding);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.NotFound,
            (await harness.Service.MutateAsync(command)).Status);
        var valid = Assert.Single(harness.Store.Commits).Mutation.Operation;
        var hostile = scenario switch
        {
            0 => valid with { ExpectedBinding = null },
            1 => valid with { ExpectedLifecycleVersion = 0 },
            2 => valid with { ExpectedLifecycleStatus = HumanInputRequestLifecycleStatus.Unknown },
            3 => valid with { ExpectedRequest = null },
            _ => valid with { ExpectedBinding = valid.ExpectedBinding! with { RunId = "changed-run" } },
        };
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Store.ReadForMutationOverride = (_, _, _, _, _) => Task.FromResult(
            new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.NotFound,
                1,
                null,
                null,
                new HumanInputRequestLifecycleStoredOperation(absent.RequestId, hostile)));

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

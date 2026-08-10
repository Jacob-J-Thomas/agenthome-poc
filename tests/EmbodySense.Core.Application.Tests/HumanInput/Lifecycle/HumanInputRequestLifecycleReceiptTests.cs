using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleReceiptTests
{
    [Fact]
    public async Task Create_existing_persists_and_replays_lifecycle_already_exists_receipt()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "create-existing-request",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            request);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            command,
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleAlreadyExists);
        Assert.All(harness.Store.MutationReads, read => Assert.Null(read.RelatedRequestId));
    }

    [Fact]
    public async Task Missing_target_persists_and_replays_not_found_receipt()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var absent = HumanInputRequestLifecycleTestData.Request(
            requestId: "missing-request",
            requestVersionId: "missing-request-version");
        var expected = new HumanInputRequestLifecycleHead(
            1,
            absent.RequestId,
            1,
            HumanInputRequestLifecycleStatus.Pending,
            HumanInputRequestLifecycleTestData.Reference(absent),
            0,
            null,
            null,
            "imagined-create",
            HumanInputRequestLifecycleTestData.Now);
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "remind-missing-request",
            absent.RequestId,
            expected: expected);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            command,
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.NotFound,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound);
        Assert.Null(harness.Store.Snapshot(absent.RequestId));
        Assert.All(harness.Store.MutationReads, read => Assert.Null(read.RelatedRequestId));
    }

    [Fact]
    public async Task Stale_optimistic_state_persists_and_replays_conflict_receipt()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var actual = harness.Store.Snapshot(request.RequestId)!.Head;
        var stale = actual with { LifecycleVersion = actual.LifecycleVersion + 1 };
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "remind-stale-request",
            request.RequestId,
            expected: stale);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            command,
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict);
        Assert.All(harness.Store.MutationReads, read => Assert.Null(read.RelatedRequestId));
    }

    [Fact]
    public async Task Terminal_target_with_stale_pending_expectation_persists_and_replays_optimistic_conflict()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        var cancel = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            "cancel-before-terminal-check",
            request.RequestId);
        Assert.Equal(
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(cancel)).Status);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var terminal = harness.Store.Snapshot(request.RequestId)!.Head;
        var expectedPending = terminal with { Status = HumanInputRequestLifecycleStatus.Pending };
        var remind = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "remind-terminal-request",
            request.RequestId,
            expected: expectedPending);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            remind,
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict);
    }

    [Fact]
    public async Task Command_built_from_current_terminal_head_is_invalid_before_observation()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        var cancel = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            "cancel-before-current-terminal-command",
            request.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(cancel)).Status);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "command-from-current-terminal-head",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Invalid, result.Status);
        Assert.Contains(
            result.ValidationErrors,
            error => error.Code == HumanInputRequestLifecycleMutationValidationErrorCode.InvalidExpectedState);
        Assert.Null(result.Proof);
        Assert.Empty(harness.Store.MutationReads);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reroute)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Amend)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Supersede)]
    public async Task Invalid_candidate_semantics_persist_and_replay_candidate_conflict(
        HumanInputRequestLifecycleOperationKind kind)
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = kind == HumanInputRequestLifecycleOperationKind.Supersede
            ? HumanInputRequestHash.Apply(HumanInputRequestLifecycleTestData.Request() with
            {
                PrivacyClass = EmbodySense.Core.Common.HumanInput.Models.HumanInputPrivacyClass.Sensitive,
                RequestHash = string.Empty,
            })
            : HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var candidate = kind switch
        {
            HumanInputRequestLifecycleOperationKind.Reroute => HumanInputRequestHash.Apply(
                HumanInputRequestLifecycleTransitionTestSupport.RerouteCandidate(request) with
                {
                    Prompt = "Reroute must not change this private prompt.",
                    RequestHash = string.Empty,
                }),
            HumanInputRequestLifecycleOperationKind.Amend => HumanInputRequestHash.Apply(request with
            {
                RequestVersionId = "request-version-unchanged-amend",
                RequestHash = string.Empty,
            }),
            _ => HumanInputRequestHash.Apply(
                HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(request) with
                {
                    PrivacyClass = EmbodySense.Core.Common.HumanInput.Models.HumanInputPrivacyClass.Private,
                    RequestHash = string.Empty,
                }),
        };
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            kind,
            $"invalid-{kind.ToString().ToLowerInvariant()}-candidate",
            request.RequestId,
            candidate);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            command,
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
    }

    [Fact]
    public async Task Delivery_transition_after_endpoint_persists_and_replays_timing_conflict()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request(
            expiresAtUtc: HumanInputRequestLifecycleTestData.Now.AddMinutes(5));
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Resolver.Handler = (_, _) => HumanInputRequestLifecycleTestData.ActiveResolution(
            harness.Grant,
            request.Timing.ExpiresAtUtc.AddTicks(1));
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "remind-after-endpoint",
            request.RequestId);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            command,
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict);
    }

    [Fact]
    public async Task Cleanup_time_before_current_head_fails_without_persisting_intent()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Time.Value = HumanInputRequestLifecycleTestData.Now.AddTicks(-1);
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Reject,
            "reject-before-current-head",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.Unavailable,
            result.Status);
        Assert.Null(result.Proof);
        Assert.Empty(harness.Store.Commits);
        Assert.Empty(harness.Resolver.Calls);
    }

    [Fact]
    public async Task Supersede_existing_related_lifecycle_persists_paired_candidate_conflict_and_replays_it()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var original = HumanInputRequestLifecycleTestData.Request();
        var replacement = HumanInputRequestLifecycleTransitionTestSupport.SupersedeCandidate(original);
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, original, "seed-original-request");
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, replacement, "seed-related-request");
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Supersede,
            "supersede-existing-related",
            original.RequestId,
            replacement);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            command,
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.Conflict,
            HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
        Assert.All(harness.Store.MutationReads, read => Assert.Equal(replacement.RequestId, read.RelatedRequestId));
        var evidence = Assert.Single(harness.Store.Commits).Mutation.Operation;
        Assert.Equal(evidence, harness.Store.Snapshot(original.RequestId)!.Operations[^1]);
        Assert.Equal(evidence, harness.Store.Snapshot(replacement.RequestId)!.Operations[^1]);
    }
}

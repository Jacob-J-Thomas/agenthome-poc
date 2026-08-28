using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

public sealed class HumanInputResponseLifecycleAuthorityRecoveryTests
{
    [Fact]
    public async Task Cross_workspace_intent_is_rejected_before_authentication_and_same_workspace_staleness_is_durable()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        var head = harness.Store.CurrentSnapshot!.Request.Head;
        var crossWorkspace = HumanInputResponseLifecycleTestData.Submit(
            harness.Request,
            head,
            "cross-workspace-submit",
            "cross-workspace-response",
            expectedBinding: harness.Request.Binding with { WorkspaceId = "workspace-sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" });

        var rejected = await harness.Service.MutateAsync(crossWorkspace);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, rejected.Status);
        Assert.Empty(harness.Authenticator.Requests);
        Assert.Empty(harness.Store.Commits);

        var sameWorkspaceStale = HumanInputResponseLifecycleTestData.Submit(
            harness.Request,
            head,
            "same-workspace-stale-submit",
            "same-workspace-stale-response",
            expectedBinding: harness.Request.Binding with { RunId = "other-run" });
        var stale = await harness.Service.MutateAsync(sameWorkspaceStale);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, stale.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.StaleResponse, stale.Operation!.FailureCode);
        Assert.Single(harness.Store.Commits);
    }

    [Fact]
    public async Task Exact_current_request_with_stale_lifecycle_version_persists_and_replays_optimistic_conflict()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        var head = harness.Store.CurrentSnapshot!.Request.Head;
        var command = HumanInputResponseLifecycleTestData.Submit(
            harness.Request,
            head,
            "stale-lifecycle-version",
            "stale-lifecycle-response",
            expectedLifecycleVersion: head.LifecycleVersion + 1);

        var conflict = await harness.Service.MutateAsync(command);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, conflict.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.OptimisticStateConflict, conflict.Operation!.FailureCode);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Replayed, (await harness.Service.MutateAsync(command)).Status);
        Assert.Single(harness.Store.Commits);
    }

    [Fact]
    public async Task Exact_replay_resolves_retained_old_and_never_retained_request_versions()
    {
        var oldRequest = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var retainedHarness = await HumanInputResponseLifecycleHarness.CreateAsync(oldRequest);
        var retainedCommand = HumanInputResponseLifecycleTestData.Submit(
            oldRequest,
            retainedHarness.Store.CurrentSnapshot!.Request.Head,
            "retained-old-submit",
            "retained-old-response");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await retainedHarness.Service.MutateAsync(retainedCommand)).Status);

        var amended = HumanInputRequestLifecycleTransitionTestSupport.AmendCandidate(
            oldRequest,
            "response-request-v2",
            "Provide the amended requested response.");
        var amend = HumanInputRequestLifecycleTransitionTestSupport.Command(
            retainedHarness.LifecycleHarness,
            HumanInputRequestLifecycleOperationKind.Amend,
            "amend-after-response",
            oldRequest.RequestId,
            amended);
        Assert.Equal(
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.Committed,
            (await retainedHarness.LifecycleHarness.Service.MutateAsync(amend)).Status);
        retainedHarness.Store.ReplaceLifecycle(retainedHarness.LifecycleHarness.Store.Snapshot(oldRequest.RequestId)!);
        retainedHarness.Time.UtcNow = oldRequest.Timing.ExpiresAtUtc.AddDays(1);

        var retainedReplay = await retainedHarness.Service.MutateAsync(retainedCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Replayed, retainedReplay.Status);
        Assert.Equal(oldRequest.RequestVersionId, retainedReplay.Operation!.RequestVersionId);

        var neverRetainedHarness = await HumanInputResponseLifecycleHarness.CreateAsync();
        var neverRetainedReference = neverRetainedHarness.Store.CurrentSnapshot!.Request.Head.CurrentRequest with
        {
            RequestVersionId = "never-retained-version",
            RequestHash = HumanInputResponseLifecycleTestData.Hash('b'),
        };
        var neverRetainedCommand = HumanInputResponseLifecycleTestData.Submit(
            neverRetainedHarness.Request,
            neverRetainedHarness.Store.CurrentSnapshot.Request.Head,
            "never-retained-submit",
            "never-retained-response",
            expectedRequest: neverRetainedReference);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, (await neverRetainedHarness.Service.MutateAsync(neverRetainedCommand)).Status);
        var neverRetainedReplay = await neverRetainedHarness.Service.MutateAsync(neverRetainedCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Replayed, neverRetainedReplay.Status);
        Assert.Equal("never-retained-version", neverRetainedReplay.Operation!.RequestVersionId);
    }

    [Fact]
    public async Task Stale_never_retained_reference_keeps_null_historical_role_after_that_exact_version_is_created()
    {
        var original = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(original);
        var laterVersion = HumanInputRequestLifecycleTransitionTestSupport.AmendCandidate(
            original,
            "future-exact-version",
            "Future exact prompt.");
        var staleCommand = HumanInputResponseLifecycleTestData.Submit(
            original,
            harness.Store.CurrentSnapshot!.Request.Head,
            "stale-before-exact-version",
            "stale-before-exact-response",
            expectedRequest: HumanInputResponseLifecycleTestData.Reference(laterVersion),
            expectedBinding: laterVersion.Binding);
        var stale = await harness.Service.MutateAsync(staleCommand);
        Assert.Equal(HumanInputResponseOperationFailureCode.StaleResponse, stale.Operation!.FailureCode);
        Assert.Null(Assert.Single(harness.Store.Commits).Operation.ActorRoleId);

        var amend = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness.LifecycleHarness,
            HumanInputRequestLifecycleOperationKind.Amend,
            "create-future-exact-version",
            original.RequestId,
            laterVersion);
        Assert.Equal(
            EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.LifecycleHarness.Service.MutateAsync(amend)).Status);
        harness.Store.ReplaceLifecycle(harness.LifecycleHarness.Store.Snapshot(original.RequestId)!);

        var replayed = await harness.Service.MutateAsync(staleCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Replayed, replayed.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.StaleResponse, replayed.Operation!.FailureCode);
    }

    [Fact]
    public async Task Denied_unavailable_throwing_and_malformed_authentication_fail_closed_without_intent()
    {
        foreach (var status in new[]
        {
            HumanInputResponseActorAuthenticationStatus.Denied,
            HumanInputResponseActorAuthenticationStatus.Unavailable,
        })
        {
            var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
            harness.Authenticator.Status = status;
            var result = await harness.Service.MutateAsync(Command(harness, $"auth-{status.ToString().ToLowerInvariant()}"));
            Assert.Equal(
                status == HumanInputResponseActorAuthenticationStatus.Denied
                    ? HumanInputResponseLifecycleMutationStatus.Denied
                    : HumanInputResponseLifecycleMutationStatus.Unavailable,
                result.Status);
            Assert.Empty(harness.Store.Commits);
        }

        var throwing = await HumanInputResponseLifecycleHarness.CreateAsync();
        throwing.Authenticator.Throw = true;
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Unavailable,
            (await throwing.Service.MutateAsync(Command(throwing, "auth-throw"))).Status);
        Assert.Empty(throwing.Store.Commits);

        var malformed = await HumanInputResponseLifecycleHarness.CreateAsync();
        malformed.Authenticator.Override = request => new HumanInputResponseActorAuthentication(
            HumanInputResponseActorAuthenticationStatus.Authenticated,
            request.OperationId,
            request.CommandHash,
            request.WorkspaceId,
            request.EvaluatedAtUtc,
            null,
            HumanInputResponseLifecycleTestData.Hash('a'));
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Unavailable,
            (await malformed.Service.MutateAsync(Command(malformed, "auth-malformed"))).Status);
        Assert.Empty(malformed.Store.Commits);
    }

    [Fact]
    public async Task Invalid_clocks_reads_and_commit_dispositions_fail_closed()
    {
        foreach (var invalidTime in new[]
        {
            default(DateTimeOffset),
            new DateTimeOffset(2026, 8, 10, 13, 30, 0, TimeSpan.FromHours(-5)),
        })
        {
            var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
            harness.Time.UtcNow = invalidTime;
            var result = await harness.Service.MutateAsync(Command(harness, $"invalid-clock-{invalidTime.Offset.Ticks}"));
            Assert.Equal(HumanInputResponseLifecycleMutationStatus.Unavailable, result.Status);
            Assert.Empty(harness.Authenticator.Requests);
            Assert.Empty(harness.Store.Commits);
        }

        var invalidRead = await HumanInputResponseLifecycleHarness.CreateAsync();
        invalidRead.Store.ReadForMutationOverride = (_, _, _, _) => Task.FromResult(
            new HumanInputResponseLifecycleStoreReadResult(
                HumanInputResponseLifecycleStoreReadStatus.Unknown,
                0,
                null,
                null));
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Ambiguous,
            (await invalidRead.Service.MutateAsync(Command(invalidRead, "invalid-read"))).Status);
        Assert.Empty(invalidRead.Authenticator.Requests);

        var invalidCommit = await HumanInputResponseLifecycleHarness.CreateAsync();
        invalidCommit.Store.CommitOverride = (_, _) => Task.FromResult(
            new HumanInputResponseLifecycleStoreCommitResult(
                HumanInputResponseLifecycleStoreCommitStatus.Unknown,
                0,
                null,
                null));
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Ambiguous,
            (await invalidCommit.Service.MutateAsync(Command(invalidCommit, "invalid-commit"))).Status);
    }

    [Fact]
    public async Task Second_optimistic_conflict_exhausts_the_bounded_retry_budget()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        harness.Store.ConflictsRemaining = 2;

        var result = await harness.Service.MutateAsync(Command(harness, "conflict-exhaustion"));

        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, result.Status);
        Assert.Equal(2, harness.Store.Commits.Count);
        Assert.Equal(2, harness.Authenticator.Requests.Count);
        Assert.Null(result.Operation);
    }

    [Fact]
    public async Task Concurrent_exact_command_race_commits_once_and_proves_the_loser_as_replay()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        var secondAuthenticator = new RecordingHumanInputResponseActorAuthenticator();
        var secondTime = new MutableHumanInputResponseTimeProvider(harness.Time.UtcNow.AddTicks(1));
        var secondService = new HumanInputResponseLifecycleService(
            harness.Store,
            secondAuthenticator,
            new StubCapabilityAuthorityTransaction(),
            "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            secondTime);
        using var barrier = new Barrier(2);
        harness.Store.ReadyReadBarrier = barrier;
        var command = Command(harness, "concurrent-exact-command");

        var firstTask = Task.Run(() => harness.Service.MutateAsync(command));
        var secondTask = Task.Run(() => secondService.MutateAsync(command));
        var results = await Task.WhenAll(firstTask, secondTask);
        harness.Store.ReadyReadBarrier = null;

        Assert.Contains(results, result => result.Status == HumanInputResponseLifecycleMutationStatus.Committed);
        Assert.Contains(results, result => result.Status == HumanInputResponseLifecycleMutationStatus.Replayed);
        Assert.Single(harness.Store.CurrentSnapshot!.Responses);
        Assert.Single(harness.Store.CurrentSnapshot.Operations);
        Assert.Equal(2, harness.Store.Commits.Count);

        var changedIntent = HumanInputResponseLifecycleCommandHash.Apply(command with
        {
            Value = HumanInputResponseLifecycleTestData.Text("changed value"),
            CommandHash = string.Empty,
        });
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Conflict,
            (await harness.Service.MutateAsync(changedIntent)).Status);
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Denied,
            (await harness.Service.MutateAsync(command)).Status);
    }

    [Fact]
    public async Task Caller_cancellation_after_durable_intent_cannot_erase_the_committed_result()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        harness.Store.AfterDurableCommit = cancellation.Cancel;

        var result = await harness.Service.MutateAsync(Command(harness, "cancel-after-intent"), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, result.Status);
        Assert.NotNull(result.Operation);
        Assert.False(harness.Store.LastCommitTokenCanBeCanceled);
        Assert.Single(harness.Store.CurrentSnapshot!.Responses);
    }

    private static HumanInputResponseLifecycleCommand Command(HumanInputResponseLifecycleHarness harness, string operationId)
        => HumanInputResponseLifecycleTestData.Submit(
            harness.Request,
            harness.Store.CurrentSnapshot!.Request.Head,
            operationId,
            $"{operationId}-response");
}

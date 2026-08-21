using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Inference.Profiles;
using EmbodySense.Core.Persistence.Inference.Profiles.Models;
using EmbodySense.Tests.Support;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbodySense.Core.Persistence.Tests.Inference.Profiles;

public sealed class GovernedModelUsageLedgerStoreTests
{
    [Fact]
    public async Task Run_read_is_authenticated_bounded_and_never_exposes_another_run()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 30, 50);
        var store = Store(workspace, paths);
        var first = GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-one", runId: "run-one");
        var second = GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-two", visitOrdinal: 2, attemptNumber: 2, runId: "run-one");
        var other = GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-three", runId: "run-two");

        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.ReserveAsync(Request(first, policy))).Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.ReserveAsync(Request(second, policy, 'e'))).Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.ReserveAsync(Request(other, policy, 'f'))).Status);

        var workspaceId = EmbodySense.Core.Application.Capabilities.CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var read = await Store(workspace, paths).ReadRunAsync(workspaceId, "run-one");
        var missing = await store.ReadRunAsync(workspaceId, "run-missing");
        var crossWorkspace = await store.ReadRunAsync("workspace-sha256:" + new string('0', 64), "run-one");
        var malformed = await store.ReadRunAsync(workspaceId, "../run-one");

        Assert.Equal(GovernedModelUsageLedgerReadStatus.Found, read.Status);
        Assert.Equal(3, read.WorkspaceGeneration);
        Assert.Equal(["attempt-one", "attempt-two"], read.Entries.Select(entry => entry.Identity.AttemptOperationId));
        Assert.All(read.Entries, entry => Assert.Equal("run-one", entry.Identity.RunId));
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedModelUsageLedgerEntry>)read.Entries).Clear());
        Assert.Equal(GovernedModelUsageLedgerReadStatus.NotFound, missing.Status);
        Assert.Equal(3, missing.WorkspaceGeneration);
        Assert.Equal(GovernedModelUsageLedgerReadStatus.Unavailable, crossWorkspace.Status);
        Assert.Equal(GovernedModelUsageLedgerReadStatus.Unavailable, malformed.Status);
    }

    [Fact]
    public async Task Full_segments_rotate_without_discarding_history_or_disabling_later_runs()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 100, 100);
        var options = new GovernedModelUsageLedgerStoreOptions { MaxEntries = 2 };
        var store = Store(workspace, paths, options);
        var firstIdentity = GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-one", runId: "run-one");
        var first = (await store.ReserveAsync(Request(firstIdentity, policy))).ReservationEntry!;
        var dispatch = GovernedModelUsagePersistenceTestData.Dispatch(first, started: true);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(dispatch, 1)).Status);
        var secondIdentity = GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-two", runId: "run-two");
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.ReserveAsync(Request(secondIdentity, policy, 'e'))).Status);
        var usage = GovernedModelUsagePersistenceTestData.PartialUsage(4);
        var observed = GovernedModelUsageLedgerEntry.Create(1, firstIdentity, 3, GovernedModelUsageLedgerPhase.UsageObserved, first.Reservation, usage, null, null, false, GovernedModelUsagePersistenceTestData.Hash('f'), dispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(observed, 2)).Status);
        var thirdIdentity = GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-three", runId: "run-three");
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.ReserveAsync(Request(thirdIdentity, policy, '1'))).Status);

        var restarted = Store(workspace, paths, options);
        var workspaceId = EmbodySense.Core.Application.Capabilities.CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var firstRun = await restarted.ReadRunAsync(workspaceId, "run-one");
        var secondRun = await restarted.ReadRunAsync(workspaceId, "run-two");
        var thirdRun = await restarted.ReadRunAsync(workspaceId, "run-three");

        Assert.Equal(GovernedModelUsageLedgerReadStatus.Found, firstRun.Status);
        Assert.Equal([GovernedModelUsageLedgerPhase.ReservationCommitted, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, GovernedModelUsageLedgerPhase.UsageObserved], firstRun.Entries.Select(entry => entry.Phase));
        Assert.Single(secondRun.Entries);
        Assert.Single(thirdRun.Entries);
        Assert.All(new[] { firstRun, secondRun, thirdRun }, read => Assert.Equal(5, read.WorkspaceGeneration));
        Assert.True(File.Exists(SegmentPath(paths, 0)));
        Assert.True(File.Exists(SegmentPath(paths, 1)));
        using var current = JsonDocument.Parse(await File.ReadAllTextAsync(LedgerPath(paths)));
        Assert.Equal(2, current.RootElement.GetProperty("segmentIndex").GetInt64());
        Assert.Equal(1, current.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Missing_or_substituted_archived_segment_fails_closed_without_mutating_the_live_head()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 100, 100);
        var options = new GovernedModelUsageLedgerStoreOptions { MaxEntries = 1 };
        var store = Store(workspace, paths, options);
        var firstIdentity = GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-one", runId: "run-one");
        var secondIdentity = GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-two", runId: "run-two");
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.ReserveAsync(Request(firstIdentity, policy))).Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.ReserveAsync(Request(secondIdentity, policy, 'e'))).Status);
        var liveBefore = await File.ReadAllTextAsync(LedgerPath(paths));
        await File.WriteAllTextAsync(SegmentPath(paths, 0), "{}\n");

        var restarted = Store(workspace, paths, options);
        var workspaceId = EmbodySense.Core.Application.Capabilities.CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var read = await restarted.ReadRunAsync(workspaceId, "run-one");
        var mutation = await restarted.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-three", runId: "run-three"), policy, 'f'));

        Assert.Equal(GovernedModelUsageLedgerReadStatus.Unavailable, read.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Unavailable, mutation.Status);
        Assert.Equal(liveBefore, await File.ReadAllTextAsync(LedgerPath(paths)));
    }

    [Fact]
    public async Task Reservation_is_server_derived_from_atomic_node_and_run_remaining_budget()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 15, 20);
        var store = Store(workspace, paths);

        var first = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy), policy));
        var second = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-two", visitOrdinal: 2, attemptNumber: 2), policy, 'e'));
        var nodeExhausted = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-three", visitOrdinal: 3, attemptNumber: 3), policy, 'f'));
        var otherNode = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-four", "inference-two", 1, 1), policy, '1'));
        var runExhausted = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-five", "inference-three", 2, 1), policy, '2'));

        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, first.Status);
        Assert.Equal(10, first.ReservationEntry?.Reservation?.InputTokens.Maximum);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, second.Status);
        Assert.Equal(5, second.ReservationEntry?.Reservation?.InputTokens.Maximum);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.BudgetExhausted, nodeExhausted.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, otherNode.Status);
        Assert.Equal(5, otherNode.ReservationEntry?.Reservation?.InputTokens.Maximum);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.BudgetExhausted, runExhausted.Status);
    }

    [Fact]
    public async Task Outer_only_bound_reserves_current_remaining_and_fully_unbounded_reserves_zero()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var outerOnly = GovernedModelUsagePersistenceTestData.InputBudget(null, 25, 25);
        var store = Store(workspace, paths);

        var bounded = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, outerOnly), outerOnly));
        var exhausted = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, outerOnly, "attempt-two", "inference-two", 1, 1), outerOnly, 'e'));
        var unboundedPolicy = GovernedModelUsagePersistenceTestData.UnboundedBudget();
        var unbounded = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, unboundedPolicy, "attempt-three", "inference-three", 2, 1), unboundedPolicy, 'f'));

        Assert.Equal(25, bounded.ReservationEntry?.Reservation?.InputTokens.Maximum);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.BudgetExhausted, exhausted.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, unbounded.Status);
        Assert.False(unbounded.ReservationEntry!.Reservation!.InputTokens.IsBounded);
    }

    [Fact]
    public async Task Exact_reservation_replay_is_idempotent_and_cross_identity_operation_reuse_conflicts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 20, 30);
        var identity = GovernedModelUsagePersistenceTestData.Identity(paths, policy);
        var request = Request(identity, policy);
        var store = Store(workspace, paths);

        var first = await store.ReserveAsync(request);
        var replay = await Store(workspace, paths).ReserveAsync(request);
        var collision = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, identity.AttemptOperationId, "inference-two", 1, 1), policy));
        var proofCollision = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, identity.AttemptOperationId, authorityEvidenceHash: '4', dataPostureEvidenceHash: '5'), policy));
        var read = await Store(workspace, paths).ReadAsync(identity);

        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, first.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.AlreadyPresent, replay.Status);
        Assert.Equal(first.ReservationEntry?.ContentHash, replay.ReservationEntry?.ContentHash);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Conflict, collision.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Conflict, proofCollision.Status);
        Assert.Equal(GovernedModelUsageLedgerReadStatus.Found, read.Status);
        Assert.Single(read.Entries);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedModelUsageLedgerEntry>)read.Entries).Clear());
    }

    [Fact]
    public async Task Concurrent_same_operation_with_different_server_times_commits_once_and_restart_replays_original_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 20, 30);
        var identity = GovernedModelUsagePersistenceTestData.Identity(paths, policy);
        var firstRequest = Request(identity, policy, recordedAtUtc: GovernedModelUsagePersistenceTestData.Now);
        var laterRequest = Request(identity, policy, recordedAtUtc: GovernedModelUsagePersistenceTestData.Now.AddMinutes(1));

        var results = await Task.WhenAll(
            Store(workspace, paths).ReserveAsync(firstRequest),
            Store(workspace, paths).ReserveAsync(laterRequest));
        var replay = await Store(workspace, paths).ReserveAsync(Request(identity, policy, recordedAtUtc: GovernedModelUsagePersistenceTestData.Now.AddDays(1)));
        var read = await Store(workspace, paths).ReadAsync(identity);

        Assert.Single(results, result => result.Status == GovernedModelUsageLedgerAppendStatus.Appended);
        Assert.Single(results, result => result.Status == GovernedModelUsageLedgerAppendStatus.AlreadyPresent);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.AlreadyPresent, replay.Status);
        Assert.All(results.Append(replay), result => Assert.Equal(read.Entries[0].ContentHash, result.ReservationEntry?.ContentHash));
        Assert.Single(read.Entries);
        Assert.Contains(read.Entries[0].RecordedAtUtc, new[] { firstRequest.RecordedAtUtc, laterRequest.RecordedAtUtc });
    }

    [Fact]
    public async Task Concurrent_reservations_cannot_both_consume_one_run_remaining_amount()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 10, 10);

        var results = await Task.WhenAll(
            Store(workspace, paths).ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-one", "inference-one", 0, 1), policy)),
            Store(workspace, paths).ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-two", "inference-two", 1, 1), policy, 'e')));

        Assert.Single(results, result => result.Status == GovernedModelUsageLedgerAppendStatus.Appended);
        Assert.Single(results, result => result.Status == GovernedModelUsageLedgerAppendStatus.BudgetExhausted);
    }

    [Fact]
    public async Task Distinct_node_policies_share_one_admitted_run_pool_but_different_admissions_are_isolated()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstPolicy = GovernedModelUsagePersistenceTestData.InputBudget(6, 10, 10);
        var secondPolicy = GovernedModelUsagePersistenceTestData.InputBudget(8, 10, 10);
        var firstIdentity = GovernedModelUsagePersistenceTestData.Identity(paths, firstPolicy);
        var secondIdentity = GovernedModelUsagePersistenceTestData.Identity(paths, secondPolicy, "attempt-two", "inference-two", 1, 1);
        var store = Store(workspace, paths);

        var first = await store.ReserveAsync(Request(firstIdentity, firstPolicy));
        var sharedPool = await store.ReserveAsync(Request(secondIdentity, secondPolicy, 'e'));
        var exhausted = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, secondPolicy, "attempt-three", "inference-three", 2, 1), secondPolicy, 'f'));
        var otherAdmission = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, firstPolicy, "attempt-four", "inference-four", 3, 1, admissionReceiptHash: '8', routingAdmissionHash: '9'), firstPolicy, '1'));

        Assert.Equal(6, first.ReservationEntry?.Reservation?.InputTokens.Maximum);
        Assert.Equal(4, sharedPool.ReservationEntry?.Reservation?.InputTokens.Maximum);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.BudgetExhausted, exhausted.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, otherAdmission.Status);
        Assert.Equal(6, otherAdmission.ReservationEntry?.Reservation?.InputTokens.Maximum);
    }

    [Fact]
    public async Task Cross_currency_consumption_in_one_admitted_run_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var usdPolicy = GovernedModelUsagePersistenceTestData.MonetaryBudget("USD", 5, 10, 10);
        var eurPolicy = GovernedModelUsagePersistenceTestData.MonetaryBudget("EUR", 5, 10, 10);
        var store = Store(workspace, paths);

        var usd = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, usdPolicy), usdPolicy));
        var eur = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, eurPolicy, "attempt-two", "inference-two", 1, 1), eurPolicy, 'e'));
        var read = await store.ReadAsync(usd.ReservationEntry!.Identity);

        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, usd.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.BudgetExhausted, eur.Status);
        Assert.Single(read.Entries);
    }

    [Fact]
    public async Task Reconciled_release_reduces_effective_consumption_but_unknown_usage_retains_reservation()
    {
        using var releasedWorkspace = new TestWorkspace();
        var releasedPaths = new WorkspacePaths(releasedWorkspace.RootPath);
        var releasedPolicy = GovernedModelUsagePersistenceTestData.InputBudget(10, 15, 30);
        var releasedStore = Store(releasedWorkspace, releasedPaths);
        var releasedReservation = (await releasedStore.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(releasedPaths, releasedPolicy), releasedPolicy))).ReservationEntry!;
        var releasedDispatch = GovernedModelUsagePersistenceTestData.Dispatch(releasedReservation, started: true);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await releasedStore.AppendAsync(releasedDispatch, 1)).Status);
        var usage = GovernedModelUsagePersistenceTestData.PartialUsage(4);
        var observed = GovernedModelUsageLedgerEntry.Create(1, releasedReservation.Identity, 3, GovernedModelUsageLedgerPhase.UsageObserved, releasedReservation.Reservation, usage, null, null, false, GovernedModelUsagePersistenceTestData.Hash('f'), releasedDispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await releasedStore.AppendAsync(observed, 2)).Status);
        var used = GovernedModelUsageVector.Create(4, 0, 0, 0, null, 0);
        var released = GovernedModelUsageVector.Create(6, 0, 0, 0, null, 0);
        var reconciled = GovernedModelUsageLedgerEntry.Create(1, releasedReservation.Identity, 4, GovernedModelUsageLedgerPhase.Reconciled, releasedReservation.Reservation, usage, used, released, false, observed.ContentHash, observed.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await releasedStore.AppendAsync(reconciled, 3)).Status);
        var afterRelease = await releasedStore.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(releasedPaths, releasedPolicy, "attempt-two", visitOrdinal: 2, attemptNumber: 2), releasedPolicy, '1'));
        Assert.Equal(10, afterRelease.ReservationEntry?.Reservation?.InputTokens.Maximum);

        using var unknownWorkspace = new TestWorkspace();
        var unknownPaths = new WorkspacePaths(unknownWorkspace.RootPath);
        var unknownPolicy = GovernedModelUsagePersistenceTestData.InputBudget(10, 15, 30);
        var unknownStore = Store(unknownWorkspace, unknownPaths);
        var unknownReservation = (await unknownStore.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(unknownPaths, unknownPolicy), unknownPolicy))).ReservationEntry!;
        var unknownDispatch = GovernedModelUsagePersistenceTestData.Dispatch(unknownReservation, started: true);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await unknownStore.AppendAsync(unknownDispatch, 1)).Status);
        var unavailable = LlmInferenceUsageEvidence.Unavailable("provider-test", "v1");
        var unknownObserved = GovernedModelUsageLedgerEntry.Create(1, unknownReservation.Identity, 3, GovernedModelUsageLedgerPhase.UsageObserved, unknownReservation.Reservation, unavailable, null, null, true, GovernedModelUsagePersistenceTestData.Hash('f'), unknownDispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await unknownStore.AppendAsync(unknownObserved, 2)).Status);
        var zero = GovernedModelUsageVector.Zero;
        var unknownReconciled = GovernedModelUsageLedgerEntry.Create(1, unknownReservation.Identity, 4, GovernedModelUsageLedgerPhase.Reconciled, unknownReservation.Reservation, unavailable, zero, zero, true, unknownObserved.ContentHash, unknownObserved.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await unknownStore.AppendAsync(unknownReconciled, 3)).Status);
        var afterUnknown = await unknownStore.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(unknownPaths, unknownPolicy, "attempt-two", visitOrdinal: 2, attemptNumber: 2), unknownPolicy, '1'));
        Assert.Equal(5, afterUnknown.ReservationEntry?.Reservation?.InputTokens.Maximum);
    }

    [Fact]
    public async Task Authoritative_token_overage_is_counted_above_reservation_and_exhausts_later_attempts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 15, 15);
        var store = Store(workspace, paths);
        var reservation = (await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy), policy))).ReservationEntry!;
        var dispatch = GovernedModelUsagePersistenceTestData.Dispatch(reservation, started: true);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(dispatch, 1)).Status);
        var usage = GovernedModelUsagePersistenceTestData.PartialUsage(16);
        var observed = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 3, GovernedModelUsageLedgerPhase.UsageObserved, reservation.Reservation, usage, null, null, false, GovernedModelUsagePersistenceTestData.Hash('f'), dispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(observed, 2)).Status);
        var used = GovernedModelUsageVector.Create(16, 0, 0, 0, null, 0);
        var released = GovernedModelUsageVector.Zero;
        var attention = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 4, GovernedModelUsageLedgerPhase.AttentionRequired, reservation.Reservation, usage, used, released, true, GovernedModelUsagePersistenceTestData.Hash('1'), observed.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(attention, 3)).Status);

        var next = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-two", visitOrdinal: 2, attemptNumber: 2), policy, '2'));

        Assert.Equal(GovernedModelUsageLedgerAppendStatus.BudgetExhausted, next.Status);
    }

    [Fact]
    public async Task Authoritative_cost_overage_is_counted_above_reservation_and_exhausts_later_attempts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.MonetaryBudget("USD", 10, 15, 15);
        var store = Store(workspace, paths);
        var reservation = (await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy), policy))).ReservationEntry!;
        var dispatch = GovernedModelUsagePersistenceTestData.Dispatch(reservation, started: true);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(dispatch, 1)).Status);
        var usage = GovernedModelUsagePersistenceTestData.MonetaryUsage("USD", 16);
        var observed = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 3, GovernedModelUsageLedgerPhase.UsageObserved, reservation.Reservation, usage, null, null, false, GovernedModelUsagePersistenceTestData.Hash('f'), dispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(observed, 2)).Status);
        var used = GovernedModelUsageVector.Create(0, 0, 0, 0, "USD", 16);
        var released = GovernedModelUsageVector.Create(0, 0, 0, 0, "USD", 0);
        var attention = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 4, GovernedModelUsageLedgerPhase.AttentionRequired, reservation.Reservation, usage, used, released, true, GovernedModelUsagePersistenceTestData.Hash('1'), observed.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(attention, 3)).Status);

        var next = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-two", visitOrdinal: 2, attemptNumber: 2), policy, '2'));

        Assert.Equal(GovernedModelUsageLedgerAppendStatus.BudgetExhausted, next.Status);
    }

    [Fact]
    public async Task Dispatch_not_started_proof_releases_full_reservation_for_aggregate_enforcement()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 10, 10);
        var store = Store(workspace, paths);
        var reservation = (await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy), policy))).ReservationEntry!;
        var proof = GovernedModelUsagePersistenceTestData.Dispatch(reservation, started: false);

        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(proof, 1)).Status);
        var next = await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-two", visitOrdinal: 2, attemptNumber: 2), policy, 'f'));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, next.Status);
        Assert.Equal(10, next.ReservationEntry?.Reservation?.InputTokens.Maximum);
    }

    [Fact]
    public async Task Optimistic_append_exact_replay_and_conflict_never_rewrite_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 20, 30);
        var store = Store(workspace, paths);
        var reservation = (await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy), policy))).ReservationEntry!;
        var dispatch = GovernedModelUsagePersistenceTestData.Dispatch(reservation, started: true);
        var conflicting = GovernedModelUsagePersistenceTestData.Dispatch(reservation, started: true, evidence: 'f');

        var appended = await store.AppendAsync(dispatch, 1);
        var replay = await Store(workspace, paths).AppendAsync(dispatch, 1);
        var conflict = await store.AppendAsync(conflicting, 1);
        var stale = await store.AppendAsync(GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 3, GovernedModelUsageLedgerPhase.AttentionRequired, reservation.Reservation, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('1'), dispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2)), 1);
        var read = await store.ReadAsync(reservation.Identity);

        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, appended.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.AlreadyPresent, replay.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Conflict, conflict.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Conflict, stale.Status);
        Assert.Equal(2, read.Generation);
        Assert.Equal(dispatch.ContentHash, read.Entries[^1].ContentHash);
    }

    [Fact]
    public async Task Reconciled_success_then_disposal_attention_is_durable_at_generation_five_and_terminal()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 20, 30);
        var store = Store(workspace, paths);
        var reservation = (await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy), policy))).ReservationEntry!;
        var dispatch = GovernedModelUsagePersistenceTestData.Dispatch(reservation, started: true);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(dispatch, 1)).Status);
        var usage = GovernedModelUsagePersistenceTestData.CompleteUsage(4);
        var observed = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 3, GovernedModelUsageLedgerPhase.UsageObserved, reservation.Reservation, usage, null, null, false, GovernedModelUsagePersistenceTestData.Hash('f'), dispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(observed, 2)).Status);
        var used = GovernedModelUsageVector.Create(4, 0, 0, 4, "USD", 0);
        var released = GovernedModelUsageVector.Create(6, 0, 0, 0, null, 0);
        var reconciled = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 4, GovernedModelUsageLedgerPhase.Reconciled, reservation.Reservation, usage, used, released, false, GovernedModelUsagePersistenceTestData.Hash('1'), observed.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, (await store.AppendAsync(reconciled, 3)).Status);
        var attention = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 5, GovernedModelUsageLedgerPhase.AttentionRequired, reservation.Reservation, usage, used, released, false, GovernedModelUsagePersistenceTestData.Hash('2'), reconciled.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(4));
        var options = new GovernedModelUsageLedgerStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) => boundary == GovernedModelUsageLedgerPersistenceBoundary.PrimaryPublished
                ? ValueTask.FromException(new IOException("Injected disposal-failure process loss after primary publication."))
                : ValueTask.CompletedTask
        };

        var interrupted = await Store(workspace, paths, options).AppendAsync(attention, 4);
        var read = await Store(workspace, paths).ReadAsync(reservation.Identity);
        var replay = await Store(workspace, paths).AppendAsync(attention, 4);
        var differentTerminal = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 5, GovernedModelUsageLedgerPhase.AttentionRequired, reservation.Reservation, usage, used, released, false, GovernedModelUsagePersistenceTestData.Hash('3'), reconciled.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(4));
        var conflict = await Store(workspace, paths).AppendAsync(differentTerminal, 4);

        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, interrupted.Status);
        Assert.Equal(GovernedModelUsageLedgerReadStatus.Found, read.Status);
        Assert.Equal(5, read.Generation);
        Assert.Equal(attention.ContentHash, read.Entries[^1].ContentHash);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.AlreadyPresent, replay.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Conflict, conflict.Status);
    }

    [Theory]
    [InlineData("wrong-first-phase")]
    [InlineData("skipped-dispatch")]
    [InlineData("reservation-mutation")]
    [InlineData("phase-regression")]
    [InlineData("forged-reconciliation")]
    [InlineData("timestamp-rollback")]
    [InlineData("repeated-attention")]
    public async Task Authenticated_semantically_corrupt_histories_fail_closed_without_mutation(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 20, 30);
        var trust = new MutableModelPersistenceTrustProvider();
        var store = Store(paths, trust);
        var reservation = (await store.ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy), policy))).ReservationEntry!;
        var dispatch = GovernedModelUsagePersistenceTestData.Dispatch(reservation, started: true);
        var usage = GovernedModelUsagePersistenceTestData.PartialUsage(4);
        var observed = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 3, GovernedModelUsageLedgerPhase.UsageObserved, reservation.Reservation, usage, null, null, false, GovernedModelUsagePersistenceTestData.Hash('f'), dispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2));
        var used = GovernedModelUsageVector.Create(4, 0, 0, 0, null, 0);
        var released = GovernedModelUsageVector.Create(6, 0, 0, 0, null, 0);
        var forgedUsed = GovernedModelUsageVector.Create(5, 0, 0, 0, null, 0);
        var forgedReleased = GovernedModelUsageVector.Create(5, 0, 0, 0, null, 0);
        var forgedReconciliation = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 4, GovernedModelUsageLedgerPhase.Reconciled, reservation.Reservation, usage, forgedUsed, forgedReleased, true, GovernedModelUsagePersistenceTestData.Hash('2'), observed.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));
        var wrongFirstPhase = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 1, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, reservation.Reservation, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('3'), null, GovernedModelUsagePersistenceTestData.Now);
        var skippedDispatch = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 2, GovernedModelUsageLedgerPhase.UsageObserved, reservation.Reservation, usage, null, null, false, GovernedModelUsagePersistenceTestData.Hash('4'), reservation.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(1));
        var mutatedReservation = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 2, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, GovernedModelUsagePersistenceTestData.Ceiling(9), null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('5'), reservation.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(1));
        var phaseRegression = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 4, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, reservation.Reservation, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('6'), observed.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));
        var timestampRollback = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 2, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, reservation.Reservation, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('7'), reservation.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(-1));
        var attention = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 3, GovernedModelUsageLedgerPhase.AttentionRequired, reservation.Reservation, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('8'), dispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2));
        var repeatedAttention = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 4, GovernedModelUsageLedgerPhase.AttentionRequired, reservation.Reservation, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('9'), attention.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));
        IReadOnlyList<GovernedModelUsageLedgerEntry> corruptHistory = corruption switch
        {
            "wrong-first-phase" => [wrongFirstPhase],
            "skipped-dispatch" => [reservation, skippedDispatch],
            "reservation-mutation" => [reservation, mutatedReservation],
            "phase-regression" => [reservation, dispatch, observed, phaseRegression],
            "forged-reconciliation" => [reservation, dispatch, observed, forgedReconciliation],
            "timestamp-rollback" => [reservation, timestampRollback],
            "repeated-attention" => [reservation, dispatch, attention, repeatedAttention],
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        Assert.False(GovernedModelUsageLedgerHistoryValidator.IsValid(corruptHistory, reservation.Identity, corruptHistory.Count));

        await RewriteAuthenticatedHistoryAsync(paths, trust, corruptHistory);
        var before = await File.ReadAllTextAsync(LedgerPath(paths));
        var read = await Store(paths, trust).ReadAsync(reservation.Identity);
        var mutation = await Store(paths, trust).ReserveAsync(Request(GovernedModelUsagePersistenceTestData.Identity(paths, policy, "attempt-two", visitOrdinal: 2, attemptNumber: 2), policy, '3'));

        Assert.Equal(GovernedModelUsageLedgerReadStatus.Unavailable, read.Status);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Unavailable, mutation.Status);
        Assert.Equal(before, await File.ReadAllTextAsync(LedgerPath(paths)));
    }

    [Fact]
    public void Shared_history_validator_rejects_every_store_level_transition_corruption_case()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 20, 30);
        var identity = GovernedModelUsagePersistenceTestData.Identity(paths, policy);
        var ceiling = GovernedModelUsagePersistenceTestData.Ceiling(10);
        var reservation = GovernedModelUsageLedgerEntry.Create(1, identity, 1, GovernedModelUsageLedgerPhase.ReservationCommitted, ceiling, null, null, null, false, GovernedModelUsagePersistenceTestData.Hash('d'), null, GovernedModelUsagePersistenceTestData.Now);
        var dispatch = GovernedModelUsagePersistenceTestData.Dispatch(reservation, started: true);
        var usage = GovernedModelUsagePersistenceTestData.PartialUsage(4);
        var observed = GovernedModelUsageLedgerEntry.Create(1, identity, 3, GovernedModelUsageLedgerPhase.UsageObserved, ceiling, usage, null, null, false, GovernedModelUsagePersistenceTestData.Hash('f'), dispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2));
        var used = GovernedModelUsageVector.Create(4, 0, 0, 0, null, 0);
        var released = GovernedModelUsageVector.Create(6, 0, 0, 0, null, 0);
        var reconciled = GovernedModelUsageLedgerEntry.Create(1, identity, 4, GovernedModelUsageLedgerPhase.Reconciled, ceiling, usage, used, released, true, GovernedModelUsagePersistenceTestData.Hash('1'), observed.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));
        var attention = GovernedModelUsageLedgerEntry.Create(1, identity, 3, GovernedModelUsageLedgerPhase.AttentionRequired, ceiling, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('2'), dispatch.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(2));

        var wrongFirstPhase = GovernedModelUsageLedgerEntry.Create(1, identity, 1, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, ceiling, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('3'), null, GovernedModelUsagePersistenceTestData.Now);
        var skippedDispatch = GovernedModelUsageLedgerEntry.Create(1, identity, 2, GovernedModelUsageLedgerPhase.UsageObserved, ceiling, usage, null, null, false, GovernedModelUsagePersistenceTestData.Hash('4'), reservation.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(1));
        var mutatedCeiling = GovernedModelUsagePersistenceTestData.Ceiling(9);
        var mutatedReservation = GovernedModelUsageLedgerEntry.Create(1, identity, 2, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, mutatedCeiling, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('5'), reservation.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(1));
        var phaseRegression = GovernedModelUsageLedgerEntry.Create(1, identity, 4, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, ceiling, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('6'), observed.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));
        var timestampRollback = GovernedModelUsageLedgerEntry.Create(1, identity, 2, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, ceiling, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('7'), reservation.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(-1));
        var repeatedAttention = GovernedModelUsageLedgerEntry.Create(1, identity, 4, GovernedModelUsageLedgerPhase.AttentionRequired, ceiling, null, null, null, true, GovernedModelUsagePersistenceTestData.Hash('8'), attention.ContentHash, GovernedModelUsagePersistenceTestData.Now.AddSeconds(3));

        Assert.False(GovernedModelUsageLedgerHistoryValidator.IsValid([wrongFirstPhase], identity, 1));
        Assert.False(GovernedModelUsageLedgerHistoryValidator.IsValid([reservation, skippedDispatch], identity, 2));
        Assert.False(GovernedModelUsageLedgerHistoryValidator.IsValid([reservation, mutatedReservation], identity, 2));
        Assert.False(GovernedModelUsageLedgerHistoryValidator.IsValid([reservation, dispatch, observed, phaseRegression], identity, 4));
        Assert.False(GovernedModelUsageLedgerHistoryValidator.IsValid([reservation, timestampRollback], identity, 2));
        Assert.False(GovernedModelUsageLedgerHistoryValidator.IsValid([reservation, dispatch, attention, repeatedAttention], identity, 4));
    }

    [Fact]
    public async Task Restart_and_interrupted_primary_publication_recover_without_duplicate_reservation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = GovernedModelUsagePersistenceTestData.InputBudget(10, 20, 30);
        var identity = GovernedModelUsagePersistenceTestData.Identity(paths, policy);
        var request = Request(identity, policy);
        var options = new GovernedModelUsageLedgerStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) => boundary == GovernedModelUsageLedgerPersistenceBoundary.PrimaryPublished
                ? ValueTask.FromException(new IOException("Injected process loss after primary publication."))
                : ValueTask.CompletedTask
        };

        var interrupted = await Store(workspace, paths, options).ReserveAsync(request);
        var read = await Store(workspace, paths).ReadAsync(identity);
        var replay = await Store(workspace, paths).ReserveAsync(request);

        Assert.Equal(GovernedModelUsageLedgerAppendStatus.Appended, interrupted.Status);
        Assert.Equal(GovernedModelUsageLedgerReadStatus.Found, read.Status);
        Assert.Single(read.Entries);
        Assert.Equal(interrupted.ReservationEntry?.ContentHash, read.Entries[0].ContentHash);
        Assert.Equal(GovernedModelUsageLedgerAppendStatus.AlreadyPresent, replay.Status);
    }

    private static GovernedModelUsageReservationRequest Request(GovernedModelUsageLedgerIdentity identity, GovernedModelBudgetPolicy policy, char evidence = 'd', DateTimeOffset? recordedAtUtc = null)
        => new(identity, policy, GovernedModelUsagePersistenceTestData.Hash(evidence), recordedAtUtc ?? GovernedModelUsagePersistenceTestData.Now);

    private static GovernedModelUsageLedgerStore Store(TestWorkspace workspace, WorkspacePaths paths, GovernedModelUsageLedgerStoreOptions? options = null)
        => new(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath), options);

    private static GovernedModelUsageLedgerStore Store(WorkspacePaths paths, MutableModelPersistenceTrustProvider trust)
        => new(paths, trust);

    private static string LedgerPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "execution", "model-usage", "ledger.json");

    private static string SegmentPath(WorkspacePaths paths, long segmentIndex)
        => Path.Combine(paths.AgentPath, "loops", "execution", "model-usage", "segments", $"segment-{segmentIndex:D20}.json");

    private static async Task RewriteAuthenticatedHistoryAsync(WorkspacePaths paths, MutableModelPersistenceTrustProvider trust, IReadOnlyList<GovernedModelUsageLedgerEntry> entries)
    {
        var path = LedgerPath(paths);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["generation"] = entries.Count;
        root["entries"] = JsonSerializer.SerializeToNode(entries, _hashJsonOptions);
        root["contentDigest"] = string.Empty;
        root["authenticationTag"] = string.Empty;
        var digest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(root.ToJsonString(_hashJsonOptions))).Value;
        var workspaceIdentity = root["workspaceIdentity"]!.GetValue<string>();
        root["contentDigest"] = digest;
        root["authenticationTag"] = MutableModelPersistenceTrustProvider.AuthenticationTag;
        await File.WriteAllTextAsync(path, root.ToJsonString(_writeJsonOptions) + "\n");
        trust.SetCurrent(workspaceIdentity, entries.Count, digest);
    }

    private static readonly JsonSerializerOptions _hashJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions _writeJsonOptions = new(_hashJsonOptions) { WriteIndented = true };
}

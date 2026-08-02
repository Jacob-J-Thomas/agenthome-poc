using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Triggers;

public sealed class TriggerQueueStoreTests
{
    private const string CrossProcessWorkspace = "EMBODYSENSE_TRIGGER_QUEUE_WORKSPACE";
    private const string CrossProcessGate = "EMBODYSENSE_TRIGGER_QUEUE_GATE";
    private const string CrossProcessOutput = "EMBODYSENSE_TRIGGER_QUEUE_OUTPUT";
    private const string CrossProcessReady = "EMBODYSENSE_TRIGGER_QUEUE_READY";
    private const string CrossProcessDelivery = "EMBODYSENSE_TRIGGER_QUEUE_DELIVERY";
    private const string CrossProcessDeduplication = "EMBODYSENSE_TRIGGER_QUEUE_DEDUPLICATION";
    private const string CrossProcessLoop = "EMBODYSENSE_TRIGGER_QUEUE_LOOP";
    private const string CrossProcessCrashAfterStaged = "EMBODYSENSE_TRIGGER_QUEUE_CRASH_AFTER_STAGED";
    private const string CrossProcessCrashAfterPrecursor = "EMBODYSENSE_TRIGGER_QUEUE_CRASH_AFTER_PRECURSOR";

    [Fact]
    public async Task Windows_staging_path_identity_check_allows_publication_and_prior_generation_cleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);

        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2")));

        var snapshot = await store.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));
        Assert.Equal(2, snapshot.QueuedEntries);
        Assert.Single(Directory.EnumerateFiles(QueueRoot(paths), "ledger-*.json"));
        Assert.DoesNotContain(Directory.EnumerateFiles(QueueRoot(paths)), path => Path.GetFileName(path).StartsWith(".staged-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(".discard-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(".cleanup-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Queue_lock_reopens_across_store_instances_and_is_owner_only_on_unix()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        var first = await new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));
        var second = await new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(4));

        Assert.Empty(first.Entries);
        Assert.Empty(second.Entries);
        if (!OperatingSystem.IsWindows())
        {
            var lockMode = File.GetUnixFileMode(Path.Combine(QueueRoot(paths), ".queue.lock"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, lockMode);
        }
    }

    [Fact]
    public async Task Admitted_entry_survives_restart_and_exact_retry_replays_without_payload_duplication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstStore = new TriggerQueueStore(paths);
        var envelope = TriggerQueueTestData.Envelope();

        var admitted = await TriggerQueueTestData.Service(firstStore).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        var restartedStore = new TriggerQueueStore(paths);
        var replayed = await TriggerQueueTestData.Service(restartedStore).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        var snapshot = await restartedStore.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));

        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admitted.Status);
        Assert.Equal(TriggerQueueAdmissionStatus.Replayed, replayed.Status);
        Assert.Equal(TriggerQueueEntryState.Queued, replayed.Entry!.State);
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(TriggerQueueEntryState.Queued, entry.State);
        Assert.Equal(1, snapshot.QueuedEntries);
        Assert.DoesNotContain(typeof(TriggerQueueAdmissionResult).GetProperties(), property => property.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
        Assert.Single(Directory.EnumerateFiles(QueueRoot(paths), "ledger-*.json"));
    }

    [Fact]
    public async Task Authorized_not_before_delivery_is_durable_and_ordered_but_unavailable_delivery_is_not_materialized()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        var future = TriggerQueueTestData.CreatedAtUtc.AddSeconds(10);
        var envelope = TriggerQueueTestData.Envelope(temporal: TriggerQueueTestData.Temporal(notBeforeUtc: future));

        var queued = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        var replayed = await TriggerQueueTestData.Service(new TriggerQueueStore(paths)).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        var unavailableEnvelope = TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2");
        var unavailable = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(unavailableEnvelope, adapterAvailable: false));
        var snapshot = await store.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));

        Assert.Equal(TriggerQueueAdmissionStatus.Queued, queued.Status);
        Assert.Equal(TriggerAdmissionStatus.NotYetEligible, queued.AdmissionStatus);
        Assert.Equal(future, queued.Entry!.OrderKey.EligibleAtUtc);
        Assert.Equal(TriggerQueueAdmissionStatus.Replayed, replayed.Status);
        Assert.Equal(TriggerQueueAdmissionStatus.Unavailable, unavailable.Status);
        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public async Task Immediate_mode_never_creates_even_a_queue_lock_or_directory()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        var request = TriggerQueueAdmissionRequestFactory.Create(TriggerQueueTestData.DeliveryRequest(TriggerQueueTestData.Envelope()), TriggerQueueAdmissionMode.ImmediateOnly);

        var result = await TriggerQueueTestData.Service(store).AdmitAsync(request);

        Assert.Equal(TriggerQueueAdmissionStatus.ImmediateRejected, result.Status);
        Assert.False(Directory.Exists(QueueRoot(paths)));
    }

    [Fact]
    public async Task Future_entry_is_promoted_by_exact_revalidation_at_eligibility_without_duplication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        var future = TriggerQueueTestData.CreatedAtUtc.AddSeconds(10);
        var envelope = TriggerQueueTestData.Envelope(temporal: TriggerQueueTestData.Temporal(notBeforeUtc: future));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));

        var promoted = await TriggerQueueTestData.Service(new TriggerQueueStore(paths)).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope, evaluatedAtUtc: future));
        var snapshot = await store.GetSnapshotAsync(future);

        Assert.Equal(TriggerQueueAdmissionStatus.Replayed, promoted.Status);
        Assert.Equal(TriggerAdmissionStatus.Admitted, promoted.AdmissionStatus);
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(TriggerAdmissionStatus.Admitted, entry.AdmissionStatus);
        Assert.Equal(2, entry.Revision);
    }

    [Fact]
    public async Task Rejected_delivery_is_terminal_evidence_and_never_counts_as_queued()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        var envelope = TriggerQueueTestData.Envelope();
        var staleRequest = TriggerQueueAdmissionRequestFactory.Create(TriggerQueueTestData.DeliveryRequest(envelope, currentLoop: TriggerQueueTestData.Loop("other-loop")));

        var rejected = await TriggerQueueTestData.Service(store).AdmitAsync(staleRequest);
        var replayed = await TriggerQueueTestData.Service(new TriggerQueueStore(paths)).AdmitAsync(staleRequest);
        var snapshot = await store.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));

        Assert.Equal(TriggerQueueAdmissionStatus.Rejected, rejected.Status);
        Assert.Equal(TriggerQueueEntryState.Rejected, rejected.Entry!.State);
        Assert.Equal(TriggerAdmissionStatus.Unauthorized, rejected.AdmissionStatus);
        Assert.Equal(TriggerQueueAdmissionStatus.Replayed, replayed.Status);
        Assert.Equal(0, snapshot.QueuedEntries);
        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public async Task Exact_retry_after_loop_revocation_terminalizes_old_queue_acceptance_but_unavailable_remains_artifact_free()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        var envelope = TriggerQueueTestData.Envelope();
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        var staleLoop = TriggerQueueAdmissionRequestFactory.Create(TriggerQueueTestData.DeliveryRequest(envelope, currentLoop: TriggerQueueTestData.Loop("replacement-loop")));

        var revoked = await TriggerQueueTestData.Service(new TriggerQueueStore(paths)).AdmitAsync(staleLoop);
        var snapshot = await store.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));

        Assert.Equal(TriggerQueueAdmissionStatus.Rejected, revoked.Status);
        Assert.Equal(TriggerAdmissionStatus.Unauthorized, revoked.AdmissionStatus);
        var terminal = Assert.Single(snapshot.Entries);
        Assert.Equal(TriggerQueueEntryState.Rejected, terminal.State);
        Assert.Equal(2, terminal.Revision);

        using var unavailableWorkspace = new TestWorkspace();
        var unavailablePaths = new WorkspacePaths(unavailableWorkspace.RootPath);
        var unavailableStore = new TriggerQueueStore(unavailablePaths);
        var unavailable = await TriggerQueueTestData.Service(unavailableStore).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope, adapterAvailable: false));
        Assert.Equal(TriggerQueueAdmissionStatus.Unavailable, unavailable.Status);
        Assert.False(Directory.Exists(QueueRoot(unavailablePaths)));
        Assert.Empty((await unavailableStore.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3))).Entries);
    }

    [Fact]
    public async Task Exact_retry_after_authority_revocation_terminalizes_old_queue_acceptance()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var envelope = TriggerQueueTestData.Envelope();
        await TriggerQueueTestData.Service(new TriggerQueueStore(paths)).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        var staleAuthority = TriggerQueueAdmissionRequestFactory.Create(TriggerQueueTestData.DeliveryRequest(envelope, currentAuthority: TriggerQueueTestData.Authority("2")));

        var revoked = await TriggerQueueTestData.Service(new TriggerQueueStore(paths)).AdmitAsync(staleAuthority);
        var terminal = Assert.Single((await new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3))).Entries);

        Assert.Equal(TriggerQueueAdmissionStatus.Rejected, revoked.Status);
        Assert.Equal(TriggerAdmissionReason.AuthorityMismatch, revoked.AdmissionReason);
        Assert.Equal(TriggerQueueEntryState.Rejected, terminal.State);
        Assert.Equal(2, terminal.Revision);
    }

    [Fact]
    public async Task Admission_reserves_terminal_and_promotion_metadata_before_accepting_a_queue_entry()
    {
        var future = TriggerQueueTestData.CreatedAtUtc.AddSeconds(10);
        var envelope = TriggerQueueTestData.Envelope(temporal: TriggerQueueTestData.Temporal(notBeforeUtc: future));
        using var sizingWorkspace = new TestWorkspace();
        var sized = await TriggerQueueTestData.Service(new TriggerQueueStore(new WorkspacePaths(sizingWorkspace.RootPath))).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        var initialBytes = sized.Entry!.SerializedEntryBytes;

        using var boundedWorkspace = new TestWorkspace();
        var quota = new TriggerQueueQuota(1, 4, initialBytes, 128 * 1024, 512 * 1024, 1);
        var boundedStore = new TriggerQueueStore(new WorkspacePaths(boundedWorkspace.RootPath), quota);
        var rejected = await TriggerQueueTestData.Service(boundedStore).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));

        Assert.Equal(TriggerQueueAdmissionStatus.Backpressured, rejected.Status);
        Assert.Equal(TriggerQueueAdmissionReason.EntryBytesExceeded, rejected.Reason);
        Assert.True(sized.Entry.RetainedReservationBytes > sized.Entry.SerializedEntryBytes);
        Assert.Empty((await boundedStore.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3))).Entries);
    }

    [Fact]
    public async Task Active_count_per_loop_and_retained_bounds_are_checked_before_materialization()
    {
        using var workspace = new TestWorkspace();
        var quota = new TriggerQueueQuota(2, 3, 128 * 1024, 128 * 1024, 384 * 1024, 1);
        var store = new TriggerQueueStore(new WorkspacePaths(workspace.RootPath), quota);
        var first = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-a")));
        var loopBackpressure = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-a")));
        var second = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-3", "dedup-3", "loop-b")));
        var retainedFull = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-4", "dedup-4", "loop-c")));
        var snapshot = await store.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));

        Assert.Equal(TriggerQueueAdmissionStatus.Queued, first.Status);
        Assert.Equal(TriggerQueueAdmissionReason.LoopQuotaExceeded, loopBackpressure.Reason);
        Assert.Equal(TriggerQueueEntryState.Backpressured, loopBackpressure.Entry!.State);
        Assert.Equal(TriggerQueueAdmissionStatus.Queued, second.Status);
        Assert.Equal(TriggerQueueAdmissionReason.RetainedEvidenceExceeded, retainedFull.Reason);
        Assert.Null(retainedFull.Entry);
        Assert.Equal(2, snapshot.QueuedEntries);
        Assert.Equal(3, snapshot.RetainedEntries);
    }

    [Fact]
    public async Task Queue_byte_quotas_count_exact_serialized_entry_metadata_not_only_envelope_content()
    {
        var firstEnvelope = TriggerQueueTestData.Envelope("delivery-a", "dedup-a", "loop-a");
        var secondEnvelope = TriggerQueueTestData.Envelope("delivery-b", "dedup-b", "loop-b");
        using var sizingWorkspace = new TestWorkspace();
        var sizingStore = new TriggerQueueStore(new WorkspacePaths(sizingWorkspace.RootPath));
        var sizingResult = await TriggerQueueTestData.Service(sizingStore).AdmitAsync(TriggerQueueTestData.QueueRequest(firstEnvelope));
        var serializedEntryBytes = sizingResult.Entry!.SerializedEntryBytes;
        Assert.True(TriggerDeliveryJson.TrySerialize(firstEnvelope, out var firstJson, out _));
        Assert.True(TriggerDeliveryJson.TrySerialize(secondEnvelope, out var secondJson, out _));
        var envelopeOnlyBytes = Encoding.UTF8.GetByteCount(firstJson!) + Encoding.UTF8.GetByteCount(secondJson!);
        Assert.True(serializedEntryBytes * 2 > envelopeOnlyBytes);

        using var boundedWorkspace = new TestWorkspace();
        var quota = new TriggerQueueQuota(2, 4, serializedEntryBytes + 64, envelopeOnlyBytes + 1, (serializedEntryBytes + 64L) * 4, 2);
        var boundedStore = new TriggerQueueStore(new WorkspacePaths(boundedWorkspace.RootPath), quota);
        var first = await TriggerQueueTestData.Service(boundedStore).AdmitAsync(TriggerQueueTestData.QueueRequest(firstEnvelope));
        var second = await TriggerQueueTestData.Service(boundedStore).AdmitAsync(TriggerQueueTestData.QueueRequest(secondEnvelope));

        Assert.Equal(TriggerQueueAdmissionStatus.Queued, first.Status);
        Assert.Equal(TriggerQueueAdmissionReason.QueueBytesExceeded, second.Reason);
        Assert.Equal(first.Entry!.SerializedEntryBytes, (await boundedStore.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3))).QueuedBytes);
    }

    [Fact]
    public async Task Priority_reuse_conflicts_and_permitted_redelivery_replays_without_a_second_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        var original = TriggerQueueTestData.Envelope();
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(original));
        var priorityConflict = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(original, TriggerQueuePriority.Critical));
        var redelivery = TriggerQueueTestData.Envelope("delivery-2", "dedup-1", "loop-1", TriggerQueueTestData.Temporal(receivedAtUtc: TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)), attempt: 2, count: 2, originalDeliveryId: "delivery-1");
        var replay = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(redelivery, evaluatedAtUtc: TriggerQueueTestData.CreatedAtUtc.AddSeconds(4)));

        Assert.Equal(TriggerQueueAdmissionReason.IdentityConflict, priorityConflict.Reason);
        Assert.Equal(TriggerQueueAdmissionStatus.Replayed, replay.Status);
        Assert.Single((await store.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(4))).Entries);
    }

    [Fact]
    public async Task Crossed_delivery_and_deduplication_matches_reject_without_merging_two_identities()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2")));

        var crossed = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-2", "loop-3")));

        Assert.Equal(TriggerQueueAdmissionStatus.Rejected, crossed.Status);
        Assert.Equal(TriggerQueueAdmissionReason.IdentityConflict, crossed.Reason);
        Assert.Equal(2, (await store.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3))).Entries.Count);
    }

    [Fact]
    public async Task Retained_byte_reservations_backpressure_before_materializing_oversized_terminal_evidence()
    {
        using var workspace = new TestWorkspace();
        var quota = new TriggerQueueQuota(2, 4, 128 * 1024, 128 * 1024, 128 * 1024, 2);
        var store = new TriggerQueueStore(new WorkspacePaths(workspace.RootPath), quota);
        var firstEnvelope = TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1", payload: TriggerQueueTestData.Payload(30 * 1024));
        var secondEnvelope = TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2", payload: TriggerQueueTestData.Payload(30 * 1024));
        var thirdEnvelope = TriggerQueueTestData.Envelope("delivery-3", "dedup-3", "loop-3", payload: TriggerQueueTestData.Payload(30 * 1024));
        var firstRequest = TriggerQueueAdmissionRequestFactory.Create(TriggerQueueTestData.DeliveryRequest(firstEnvelope, currentLoop: TriggerQueueTestData.Loop("stale-1")));
        var secondRequest = TriggerQueueAdmissionRequestFactory.Create(TriggerQueueTestData.DeliveryRequest(secondEnvelope, currentLoop: TriggerQueueTestData.Loop("stale-2")));
        var thirdRequest = TriggerQueueAdmissionRequestFactory.Create(TriggerQueueTestData.DeliveryRequest(thirdEnvelope, currentLoop: TriggerQueueTestData.Loop("stale-3")));

        var first = await TriggerQueueTestData.Service(store).AdmitAsync(firstRequest);
        var second = await TriggerQueueTestData.Service(store).AdmitAsync(secondRequest);
        var third = await TriggerQueueTestData.Service(store).AdmitAsync(thirdRequest);

        Assert.Equal(TriggerQueueAdmissionStatus.Rejected, first.Status);
        Assert.Equal(TriggerQueueAdmissionStatus.Rejected, second.Status);
        Assert.Equal(TriggerQueueAdmissionReason.RetainedEvidenceExceeded, third.Reason);
        Assert.Null(third.Entry);
    }

    [Fact]
    public async Task Same_identity_is_workspace_scoped_and_does_not_cross_contaminate_history_or_quota()
    {
        using var firstWorkspace = new TestWorkspace();
        using var secondWorkspace = new TestWorkspace();
        var envelope = TriggerQueueTestData.Envelope();
        var first = await TriggerQueueTestData.Service(new TriggerQueueStore(new WorkspacePaths(firstWorkspace.RootPath))).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        var second = await TriggerQueueTestData.Service(new TriggerQueueStore(new WorkspacePaths(secondWorkspace.RootPath))).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        Assert.Equal(TriggerQueueAdmissionStatus.Queued, first.Status);
        Assert.Equal(TriggerQueueAdmissionStatus.Queued, second.Status);
    }

    [Fact]
    public async Task Concurrent_final_slot_and_same_identity_races_are_serialized_without_duplicate_nonterminal_entries()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = new TriggerQueueQuota(1, 4, 128 * 1024, 128 * 1024, 512 * 1024, 1);
        var firstEnvelope = TriggerQueueTestData.Envelope("delivery-a", "dedup-a", "loop-a");
        var secondEnvelope = TriggerQueueTestData.Envelope("delivery-b", "dedup-b", "loop-b");
        var finalSlot = await Task.WhenAll(
            TriggerQueueTestData.Service(new TriggerQueueStore(paths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(firstEnvelope)),
            TriggerQueueTestData.Service(new TriggerQueueStore(paths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(secondEnvelope)));

        Assert.Single(finalSlot, result => result.Status == TriggerQueueAdmissionStatus.Queued);
        Assert.Single(finalSlot, result => result.Status == TriggerQueueAdmissionStatus.Backpressured);

        using var identityWorkspace = new TestWorkspace();
        var identityPaths = new WorkspacePaths(identityWorkspace.RootPath);
        var envelope = TriggerQueueTestData.Envelope();
        var sameIdentity = await Task.WhenAll(
            TriggerQueueTestData.Service(new TriggerQueueStore(identityPaths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope)),
            TriggerQueueTestData.Service(new TriggerQueueStore(identityPaths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope)));
        var snapshot = await new TriggerQueueStore(identityPaths, quota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));

        Assert.Single(sameIdentity, result => result.Status == TriggerQueueAdmissionStatus.Queued);
        Assert.Single(sameIdentity, result => result.Status == TriggerQueueAdmissionStatus.Replayed);
        Assert.Single(snapshot.Entries);
        Assert.Equal(1, snapshot.QueuedEntries);
    }

    [Fact]
    public async Task Two_process_final_slot_and_same_identity_races_have_one_deterministic_durable_winner()
    {
        using var finalWorkspace = new TestWorkspace();
        var finalStatuses = await RunCrossProcessRaceAsync(finalWorkspace.RootPath, ("delivery-a", "dedup-a", "loop-a"), ("delivery-b", "dedup-b", "loop-b"));
        Assert.Single(finalStatuses, status => status == TriggerQueueAdmissionStatus.Queued.ToString());
        Assert.Single(finalStatuses, status => status == TriggerQueueAdmissionStatus.Backpressured.ToString());
        var finalSnapshot = await new TriggerQueueStore(new WorkspacePaths(finalWorkspace.RootPath), RaceQuota()).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));
        Assert.Equal(1, finalSnapshot.QueuedEntries);
        Assert.Equal(2, finalSnapshot.RetainedEntries);
        Assert.Empty(Directory.EnumerateFileSystemEntries(finalWorkspace.RootPath, ".trigger-queue-directory-*.tmp", SearchOption.AllDirectories));

        using var identityWorkspace = new TestWorkspace();
        var same = ("delivery-same", "dedup-same", "loop-same");
        var identityStatuses = await RunCrossProcessRaceAsync(identityWorkspace.RootPath, same, same);
        Assert.Single(identityStatuses, status => status == TriggerQueueAdmissionStatus.Queued.ToString());
        Assert.Single(identityStatuses, status => status == TriggerQueueAdmissionStatus.Replayed.ToString());
        var identitySnapshot = await new TriggerQueueStore(new WorkspacePaths(identityWorkspace.RootPath), RaceQuota()).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));
        Assert.Single(identitySnapshot.Entries);
        Assert.Empty(Directory.EnumerateFileSystemEntries(identityWorkspace.RootPath, ".trigger-queue-directory-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cross_process_queue_admission_host()
    {
        var workspace = Environment.GetEnvironmentVariable(CrossProcessWorkspace);
        if (string.IsNullOrEmpty(workspace))
        {
            return;
        }

        var gate = Environment.GetEnvironmentVariable(CrossProcessGate)!;
        var output = Environment.GetEnvironmentVariable(CrossProcessOutput)!;
        var ready = Environment.GetEnvironmentVariable(CrossProcessReady)!;
        var delivery = Environment.GetEnvironmentVariable(CrossProcessDelivery)!;
        var deduplication = Environment.GetEnvironmentVariable(CrossProcessDeduplication)!;
        var loop = Environment.GetEnvironmentVariable(CrossProcessLoop)!;
        await File.WriteAllTextAsync(ready, "ready");
        var wait = Stopwatch.StartNew();
        while (!File.Exists(gate))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(15), "Cross-process trigger queue gate was not released within its bounded wait.");
            await Task.Delay(10);
        }

        ITriggerQueueDurabilityObserver? observer = null;
        if (string.Equals(Environment.GetEnvironmentVariable(CrossProcessCrashAfterStaged), "1", StringComparison.Ordinal))
        {
            observer = new CallbackObserver(onStaged: (_, _, _) => TerminateCrossProcessHost());
        }
        else if (string.Equals(Environment.GetEnvironmentVariable(CrossProcessCrashAfterPrecursor), "1", StringComparison.Ordinal))
        {
            observer = new CallbackObserver(onStagingPrecursorCreated: (_, _, _) => TerminateCrossProcessHost());
        }
        var store = new TriggerQueueStore(new WorkspacePaths(workspace), RaceQuota(), observer);
        var result = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope(delivery, deduplication, loop)));
        await File.WriteAllTextAsync(output, result.Status.ToString());
    }

    [Fact]
    public async Task Cancellation_is_revisioned_atomic_and_replayed_as_terminal_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        var envelope = TriggerQueueTestData.Envelope();
        var queued = await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        var stale = await store.CancelAsync(envelope.DeliveryId, queued.Entry!.Revision + 1, TriggerQueueTestData.CreatedAtUtc.AddSeconds(4));
        var cancelled = await store.CancelAsync(envelope.DeliveryId, queued.Entry.Revision, TriggerQueueTestData.CreatedAtUtc.AddSeconds(4));
        var repeated = await store.CancelAsync(envelope.DeliveryId, cancelled.Entry!.Revision, TriggerQueueTestData.CreatedAtUtc.AddSeconds(5));
        var replay = await TriggerQueueTestData.Service(new TriggerQueueStore(paths)).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));

        Assert.Equal(TriggerQueueCancellationStatus.RevisionConflict, stale.Status);
        Assert.Equal(TriggerQueueCancellationStatus.Cancelled, cancelled.Status);
        Assert.Equal(TriggerQueueEntryState.Cancelled, cancelled.Entry.State);
        Assert.Equal(TriggerQueueCancellationStatus.AlreadyTerminal, repeated.Status);
        Assert.Equal(TriggerQueueAdmissionStatus.Replayed, replay.Status);
        Assert.Equal(TriggerQueueEntryState.Cancelled, replay.Entry!.State);
    }

    [Fact]
    public async Task Missing_cancellation_persists_expiry_sweep_and_public_argument_guards_fail_before_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        var expiry = TriggerQueueTestData.CreatedAtUtc.AddSeconds(5);
        var envelope = TriggerQueueTestData.Envelope(temporal: TriggerQueueTestData.Temporal(expiresAtUtc: expiry));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        Assert.True(TriggerDeliveryId.TryParse("missing", out var missing));

        var notFound = await store.CancelAsync(missing!, 1, expiry);

        Assert.Equal(TriggerQueueCancellationStatus.NotFound, notFound.Status);
        Assert.Equal(TriggerQueueEntryState.Expired, Assert.Single((await new TriggerQueueStore(paths).GetSnapshotAsync(expiry)).Entries).State);
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CancelAsync(null!, 1, expiry));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CancelAsync(missing!, 0, expiry));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CancelAsync(missing!, 1, expiry.ToOffset(TimeSpan.FromHours(1))));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetSnapshotAsync(expiry.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public async Task History_lookup_propagates_cancellation_and_returns_unavailable_for_hostile_storage()
    {
        using var cancelledWorkspace = new TestWorkspace();
        var cancelledStore = new TriggerQueueStore(new WorkspacePaths(cancelledWorkspace.RootPath));
        var envelope = TriggerQueueTestData.Envelope();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledStore.FindAsync(envelope.DeliveryId, envelope.DeduplicationId, cancellation.Token));

        using var hostileWorkspace = new TestWorkspace();
        var hostilePaths = new WorkspacePaths(hostileWorkspace.RootPath);
        var hostileStore = new TriggerQueueStore(hostilePaths);
        await TriggerQueueTestData.Service(hostileStore).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        await File.WriteAllTextAsync(Assert.Single(Directory.EnumerateFiles(QueueRoot(hostilePaths), "ledger-*.json")), "hostile");
        var unavailable = await hostileStore.FindAsync(envelope.DeliveryId, envelope.DeduplicationId);

        Assert.Equal(TriggerDeliveryAdmissionHistoryLookupStatus.Unavailable, unavailable.Status);
    }

    [Fact]
    public async Task Expiry_is_exclusive_deadline_is_inclusive_and_terminalization_survives_restart()
    {
        using var expiryWorkspace = new TestWorkspace();
        var expiryPaths = new WorkspacePaths(expiryWorkspace.RootPath);
        var expiry = TriggerQueueTestData.CreatedAtUtc.AddSeconds(5);
        var expiring = TriggerQueueTestData.Envelope(temporal: TriggerQueueTestData.Temporal(expiresAtUtc: expiry));
        var expiryStore = new TriggerQueueStore(expiryPaths);
        await TriggerQueueTestData.Service(expiryStore).AdmitAsync(TriggerQueueTestData.QueueRequest(expiring));
        var expired = await expiryStore.GetSnapshotAsync(expiry);
        Assert.Equal(TriggerQueueEntryState.Expired, Assert.Single(expired.Entries).State);
        Assert.Equal(TriggerQueueTerminalReason.Expired, expired.Entries[0].TerminalReason);

        using var deadlineWorkspace = new TestWorkspace();
        var deadlinePaths = new WorkspacePaths(deadlineWorkspace.RootPath);
        var deadline = TriggerQueueTestData.CreatedAtUtc.AddSeconds(5);
        var bounded = TriggerQueueTestData.Envelope(temporal: TriggerQueueTestData.Temporal(deadlineUtc: deadline));
        var deadlineStore = new TriggerQueueStore(deadlinePaths);
        await TriggerQueueTestData.Service(deadlineStore).AdmitAsync(TriggerQueueTestData.QueueRequest(bounded));
        Assert.Equal(TriggerQueueEntryState.Queued, Assert.Single((await deadlineStore.GetSnapshotAsync(deadline)).Entries).State);
        var exceeded = await new TriggerQueueStore(deadlinePaths).GetSnapshotAsync(deadline.AddTicks(1));
        Assert.Equal(TriggerQueueTerminalReason.DeadlineExceeded, Assert.Single(exceeded.Entries).TerminalReason);
    }

    [Fact]
    public async Task Snapshot_order_is_eligibility_then_bounded_priority_then_acceptance_then_delivery_ordinal()
    {
        using var workspace = new TestWorkspace();
        var store = new TriggerQueueStore(new WorkspacePaths(workspace.RootPath), new TriggerQueueQuota(8, 16, 128 * 1024, 1024 * 1024, 2 * 1024 * 1024, 8));
        var normal = TriggerQueueTestData.Envelope("delivery-normal", "dedup-normal", "loop-a");
        var critical = TriggerQueueTestData.Envelope("delivery-critical", "dedup-critical", "loop-b");
        var future = TriggerQueueTestData.Envelope("delivery-future", "dedup-future", "loop-c", TriggerQueueTestData.Temporal(notBeforeUtc: TriggerQueueTestData.CreatedAtUtc.AddSeconds(10)));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(normal));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(critical, TriggerQueuePriority.Critical));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(future, TriggerQueuePriority.Critical));

        var snapshot = await store.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));

        Assert.Equal(["delivery-critical", "delivery-normal", "delivery-future"], snapshot.Entries.Select(entry => entry.DeliveryId.Value));
    }

    [Theory]
    [InlineData("whitespace")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("unsupported")]
    [InlineData("partial")]
    [InlineData("invalid-utf8")]
    [InlineData("quota-type")]
    [InlineData("quota-range")]
    [InlineData("quota-not-object")]
    [InlineData("entries-not-array")]
    [InlineData("entry-property-missing")]
    [InlineData("entry-revision")]
    [InlineData("receiptless-admitted")]
    [InlineData("partial-receipt")]
    [InlineData("invalid-receipt")]
    public async Task Malformed_noncanonical_or_unsupported_schema_one_ledger_fails_closed(string mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope()));
        var path = Assert.Single(Directory.EnumerateFiles(QueueRoot(paths), "ledger-*.json"));
        var content = await File.ReadAllTextAsync(path);
        await File.WriteAllBytesAsync(path, MutateLedger(mutation, content));

        await Assert.ThrowsAnyAsync<Exception>(() => new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));
    }

    [Fact]
    public async Task Unknown_artifact_generation_gap_and_quota_substitution_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = TriggerQueueQuota.Default;
        var store = new TriggerQueueStore(paths, quota);
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope()));
        var root = QueueRoot(paths);
        await File.WriteAllTextAsync(Path.Combine(root, "unknown.txt"), "hostile");
        await Assert.ThrowsAsync<FormatException>(() => new TriggerQueueStore(paths, quota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));
        File.Delete(Path.Combine(root, "unknown.txt"));

        var original = Assert.Single(Directory.EnumerateFiles(root, "ledger-*.json"));
        File.Copy(original, GenerationPath(root, 3));
        await Assert.ThrowsAsync<FormatException>(() => new TriggerQueueStore(paths, quota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));
        File.Delete(GenerationPath(root, 3));

        var differentQuota = new TriggerQueueQuota(31, 128, 128 * 1024, 4 * 1024 * 1024, 16 * 1024 * 1024, 4);
        await Assert.ThrowsAsync<FormatException>(() => new TriggerQueueStore(paths, differentQuota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));
    }

    [Fact]
    public async Task Generation_name_mismatch_more_than_two_generations_and_duplicate_cleanup_claim_fail_closed()
    {
        using var mismatchWorkspace = new TestWorkspace();
        var mismatchPaths = new WorkspacePaths(mismatchWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(mismatchPaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope()));
        var mismatchRoot = QueueRoot(mismatchPaths);
        File.Move(GenerationPath(mismatchRoot, 1), GenerationPath(mismatchRoot, 2));
        await Assert.ThrowsAsync<FormatException>(() => new TriggerQueueStore(mismatchPaths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        using var countWorkspace = new TestWorkspace();
        var countPaths = new WorkspacePaths(countWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(countPaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope()));
        var countRoot = QueueRoot(countPaths);
        File.Copy(GenerationPath(countRoot, 1), GenerationPath(countRoot, 2));
        File.Copy(GenerationPath(countRoot, 1), GenerationPath(countRoot, 3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(countPaths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        using var claimWorkspace = new TestWorkspace();
        var claimPaths = new WorkspacePaths(claimWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(claimPaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        var observer = new CallbackObserver(onCleanupClaimed: (_, _) => throw new IOException("retain cleanup claim"));
        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(claimPaths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        var claimRoot = QueueRoot(claimPaths);
        var claim = Assert.Single(Directory.EnumerateFiles(claimRoot, ".cleanup-*.tmp"));
        File.Copy(claim, GenerationPath(claimRoot, 1));
        await Assert.ThrowsAsync<FormatException>(() => new TriggerQueueStore(claimPaths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        using var combinedWorkspace = new TestWorkspace();
        var combinedPaths = new WorkspacePaths(combinedWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(combinedPaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(combinedPaths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        var combinedRoot = QueueRoot(combinedPaths);
        var combinedClaim = Assert.Single(Directory.EnumerateFiles(combinedRoot, ".cleanup-*.tmp"));
        File.Copy(GenerationPath(combinedRoot, 2), GenerationPath(combinedRoot, 3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(combinedPaths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));
        Assert.True(File.Exists(combinedClaim));
    }

    [Fact]
    public async Task Interrupted_old_generation_cleanup_restores_a_contiguous_sequence_before_loading_latest()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        var observer = new CallbackObserver(onCleanupClaimed: (_, _) => throw new IOException("interrupt cleanup"));
        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        var root = QueueRoot(paths);
        var generationOne = GenerationPath(root, 1);
        var cleanup = Assert.Single(Directory.EnumerateFiles(root, ".cleanup-*.tmp"));
        Assert.False(File.Exists(generationOne));

        var snapshot = await new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.True(File.Exists(generationOne));
        Assert.False(File.Exists(cleanup));
    }

    [Fact]
    public async Task Restart_rejects_substituted_cleanup_and_tombstone_identities_and_excess_tombstones()
    {
        using var cleanupWorkspace = new TestWorkspace();
        var cleanupPaths = new WorkspacePaths(cleanupWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(cleanupPaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        var retainCleanup = new CallbackObserver(onCleanupClaimed: (_, _) => throw new IOException("retain cleanup"));
        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(cleanupPaths, observer: retainCleanup)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        var cleanup = Assert.Single(Directory.EnumerateFiles(QueueRoot(cleanupPaths), ".cleanup-*.tmp"));
        File.Move(cleanup, Path.Combine(cleanupWorkspace.RootPath, "displaced-cleanup"));
        await File.WriteAllTextAsync(cleanup, "substituted-cleanup");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(cleanupPaths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));
        Assert.Equal("substituted-cleanup", await File.ReadAllTextAsync(cleanup));

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var quota = TriggerQueueQuota.Default with { MaxDurabilityTombstones = 2 };
        using var tombstoneWorkspace = new TestWorkspace();
        var tombstonePaths = new WorkspacePaths(tombstoneWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(tombstonePaths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        await TriggerQueueTestData.Service(new TriggerQueueStore(tombstonePaths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2")));
        var tombstone = Assert.Single(Directory.EnumerateFiles(QueueRoot(tombstonePaths), ".tombstone-*.tmp"));
        File.Move(tombstone, Path.Combine(tombstoneWorkspace.RootPath, "displaced-tombstone"));
        await File.WriteAllTextAsync(tombstone, string.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(tombstonePaths, quota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));
        Assert.Equal(0, new FileInfo(tombstone).Length);

        using var countWorkspace = new TestWorkspace();
        var countPaths = new WorkspacePaths(countWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(countPaths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        await TriggerQueueTestData.Service(new TriggerQueueStore(countPaths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2")));
        await TriggerQueueTestData.Service(new TriggerQueueStore(countPaths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-3", "dedup-3", "loop-3")));
        var countRoot = QueueRoot(countPaths);
        Assert.Equal(2, Directory.EnumerateFiles(countRoot, ".tombstone-*.tmp").Count());

        var smallerQuota = quota with { MaxDurabilityTombstones = 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(countPaths, smallerQuota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));
        Assert.Equal(2, Directory.EnumerateFiles(countRoot, ".tombstone-*.tmp").Count());
    }

    [Fact]
    public async Task Directory_entry_and_aggregate_byte_preflight_bounds_fail_before_deserialization()
    {
        using var countWorkspace = new TestWorkspace();
        var countPaths = new WorkspacePaths(countWorkspace.RootPath);
        var countRoot = QueueRoot(countPaths);
        Directory.CreateDirectory(countRoot);
        for (var index = 0; index < 129; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(countRoot, $"unknown-{index:D3}"), "x");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(countPaths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        using var bytesWorkspace = new TestWorkspace();
        var bytesPaths = new WorkspacePaths(bytesWorkspace.RootPath);
        var bytesRoot = QueueRoot(bytesPaths);
        Directory.CreateDirectory(bytesRoot);
        await File.WriteAllBytesAsync(GenerationPath(bytesRoot, 1), new byte[20_000]);
        var tinyQuota = new TriggerQueueQuota(1, 1, 1, 1, 1, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(bytesPaths, tinyQuota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));
    }

    [Fact]
    public async Task Pre_staging_cancellation_is_artifact_free_but_cancellation_during_staging_does_not_undo_publication()
    {
        using var beforeWorkspace = new TestWorkspace();
        var beforePaths = new WorkspacePaths(beforeWorkspace.RootPath);
        using var beforeCancellation = new CancellationTokenSource();
        beforeCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(beforePaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope()), beforeCancellation.Token));
        Assert.False(Directory.Exists(QueueRoot(beforePaths)));

        using var duringWorkspace = new TestWorkspace();
        var duringPaths = new WorkspacePaths(duringWorkspace.RootPath);
        using var duringCancellation = new CancellationTokenSource();
        var observer = new CallbackObserver(onStaged: (_, _, _) => duringCancellation.Cancel());
        var admitted = await TriggerQueueTestData.Service(new TriggerQueueStore(duringPaths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope()), duringCancellation.Token);
        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admitted.Status);
        Assert.Single((await new TriggerQueueStore(duringPaths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3))).Entries);
    }

    [Fact]
    public async Task Crash_before_publication_leaves_no_ledger_but_post_publication_uncertainty_replays_after_restart()
    {
        using var stagedWorkspace = new TestWorkspace();
        var stagedPaths = new WorkspacePaths(stagedWorkspace.RootPath);
        var stagedObserver = new CallbackObserver(onStaged: (_, _, _) => throw new IOException("simulated pre-publication crash"));
        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(stagedPaths, observer: stagedObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));
        Assert.Empty(Directory.EnumerateFiles(QueueRoot(stagedPaths), "ledger-*.json"));

        using var publishedWorkspace = new TestWorkspace();
        var publishedPaths = new WorkspacePaths(publishedWorkspace.RootPath);
        var publishedObserver = new CallbackObserver(onPublished: (_, _) => throw new IOException("simulated post-publication crash"));
        var envelope = TriggerQueueTestData.Envelope();
        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(publishedPaths, observer: publishedObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope)));
        var replay = await TriggerQueueTestData.Service(new TriggerQueueStore(publishedPaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(envelope));
        Assert.Equal(TriggerQueueAdmissionStatus.Replayed, replay.Status);
    }

    [Fact]
    public async Task Restart_reclaims_an_exact_authenticated_staging_artifact_after_process_death()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = QueueRoot(paths);
        var gate = Path.Combine(workspace.RootPath, "release-crashing-trigger-queue-host");
        var ready = Path.Combine(workspace.RootPath, "crashing-trigger-queue-ready");
        var output = Path.Combine(workspace.RootPath, "crashing-trigger-queue-result");
        using var process = StartCrossProcessHost(workspace.RootPath, gate, ready, output, ("delivery-crash", "dedup-crash", "loop-crash"), crashAfterStaged: true);
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, process.ExitCode);
        Assert.Single(Directory.EnumerateFiles(root, ".staged-*.tmp"));

        var restartedStore = new TriggerQueueStore(paths);
        var restarted = await restartedStore.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));
        var admitted = await TriggerQueueTestData.Service(restartedStore).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-after-restart", "dedup-after-restart", "loop-after-restart")));

        Assert.Empty(restarted.Entries);
        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admitted.Status);
        Assert.DoesNotContain(Directory.EnumerateFiles(root), path => Path.GetFileName(path).StartsWith(".staged-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(".discard-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_preserves_a_bounded_empty_precursor_after_process_death_without_wedging_later_admission()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = QueueRoot(paths);
        var gate = Path.Combine(workspace.RootPath, "release-precursor-crashing-trigger-queue-host");
        var ready = Path.Combine(workspace.RootPath, "precursor-crashing-trigger-queue-ready");
        var output = Path.Combine(workspace.RootPath, "precursor-crashing-trigger-queue-result");
        using var process = StartCrossProcessHost(workspace.RootPath, gate, ready, output, ("delivery-crash", "dedup-crash", "loop-crash"), crashAfterPrecursor: true);
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, process.ExitCode);
        var precursor = Assert.Single(Directory.EnumerateFiles(root, ".ledger-*.tmp"));
        Assert.Equal(0, new FileInfo(precursor).Length);

        var restartedStore = new TriggerQueueStore(paths);
        var restarted = await restartedStore.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));
        var admitted = await TriggerQueueTestData.Service(restartedStore).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-after-precursor", "dedup-after-precursor", "loop-after-precursor")));

        Assert.Empty(restarted.Entries);
        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admitted.Status);
        Assert.True(File.Exists(precursor));
        Assert.Equal(0, new FileInfo(precursor).Length);
    }

    [Fact]
    public async Task Precursor_boundary_write_failure_reclaims_only_the_exact_empty_file_and_does_not_wedge_retry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var observer = new CallbackObserver(onStagingPrecursorCreated: (_, _, _) => throw new IOException("simulated precursor-boundary write failure"));

        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));
        var admitted = await TriggerQueueTestData.Service(new TriggerQueueStore(paths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-retry", "dedup-retry", "loop-retry")));

        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admitted.Status);
        Assert.Empty(Directory.EnumerateFiles(QueueRoot(paths), ".ledger-*.tmp"));
    }

    [Fact]
    public async Task Precursor_mutation_at_the_publication_boundary_fails_before_ledger_publication_and_preserves_the_substitute()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = QueueRoot(paths);
        Directory.CreateDirectory(root);
        var precursor = Path.Combine(root, $".ledger-{1:D19}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(precursor, []);
        var observer = new CallbackObserver(onPublishing: (_, _, _) => File.WriteAllText(precursor, "hostile precursor mutation"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));

        Assert.Equal("hostile precursor mutation", await File.ReadAllTextAsync(precursor));
        Assert.Empty(Directory.EnumerateFiles(root, "ledger-*.json"));
    }

    [Fact]
    public async Task Precursor_mutation_after_publication_prevents_a_success_response_and_preserves_the_substitute()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = QueueRoot(paths);
        Directory.CreateDirectory(root);
        var precursor = Path.Combine(root, $".ledger-{1:D19}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(precursor, []);
        var observer = new CallbackObserver(onPublished: (_, _) => File.WriteAllText(precursor, "late hostile precursor mutation"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));

        Assert.Equal("late hostile precursor mutation", await File.ReadAllTextAsync(precursor));
        Assert.Single(Directory.EnumerateFiles(root, "ledger-*.json"));
    }

    [Fact]
    public async Task Authenticated_staging_name_with_a_different_identity_is_preserved_and_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = QueueRoot(paths);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $".staged-{1:D19}-{0UL:X16}-{1UL:X16}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(path, "unowned staging evidence");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        Assert.Equal("unowned staging evidence", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Repeated_post_publication_uncertainty_keeps_two_generations_and_remains_restart_recoverable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(paths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        var observer = new CallbackObserver(onPublished: (_, _) => throw new IOException("uncertain publication"));
        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-3", "dedup-3", "loop-3"))));

        Assert.Equal(2, Directory.EnumerateFiles(QueueRoot(paths), "ledger-*.json").Count());
        Assert.Equal(3, (await new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3))).Entries.Count);
    }

    [Fact]
    public async Task Latest_generation_predecessor_hash_prevents_cleanup_of_mutated_older_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(paths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        var observer = new CallbackObserver(onPublished: (_, _) => throw new IOException("retain predecessor"));
        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        var predecessor = GenerationPath(QueueRoot(paths), 1);
        var bytes = await File.ReadAllBytesAsync(predecessor);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(predecessor, bytes);

        await Assert.ThrowsAsync<FormatException>(() => new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        Assert.True(File.Exists(predecessor));
    }

    [Fact]
    public async Task Unowned_well_named_staging_artifact_is_preserved_and_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = QueueRoot(paths);
        Directory.CreateDirectory(root);
        var artifact = Path.Combine(root, $".ledger-{1:D19}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(artifact, "user-owned");

        await Assert.ThrowsAsync<FormatException>(() => new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        Assert.Equal("user-owned", await File.ReadAllTextAsync(artifact));
    }

    [Fact]
    public async Task Destination_and_staging_substitution_never_overwrite_or_delete_the_replacement()
    {
        using var destinationWorkspace = new TestWorkspace();
        var destinationPaths = new WorkspacePaths(destinationWorkspace.RootPath);
        const string DestinationReplacement = "do-not-overwrite";
        string? destination = null;
        var destinationObserver = new CallbackObserver(onPublishing: (_, _, path) =>
        {
            destination = path;
            File.WriteAllText(path, DestinationReplacement);
        });
        await Assert.ThrowsAnyAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(destinationPaths, observer: destinationObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));
        Assert.NotNull(destination);
        Assert.Equal(DestinationReplacement, await File.ReadAllTextAsync(destination!));

        using var stagingWorkspace = new TestWorkspace();
        var stagingPaths = new WorkspacePaths(stagingWorkspace.RootPath);
        const string StagingReplacement = "do-not-delete";
        string? replacementPath = null;
        var stagingObserver = new CallbackObserver(onStaged: (_, staging, _) =>
        {
            replacementPath = staging;
            File.Delete(staging);
            File.WriteAllText(staging, StagingReplacement);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(stagingPaths, observer: stagingObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));
        Assert.NotNull(replacementPath);
        Assert.Equal(StagingReplacement, await File.ReadAllTextAsync(replacementPath!));

        using var boundaryWorkspace = new TestWorkspace();
        var boundaryPaths = new WorkspacePaths(boundaryWorkspace.RootPath);
        const string BoundaryReplacement = "do-not-publish";
        string? boundaryReplacementPath = null;
        var boundaryObserver = new CallbackObserver(onPublishing: (_, staging, _) =>
        {
            boundaryReplacementPath = staging;
            File.Move(staging, Path.Combine(boundaryWorkspace.RootPath, "displaced-staging"));
            File.WriteAllText(staging, BoundaryReplacement);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(boundaryPaths, observer: boundaryObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));
        Assert.NotNull(boundaryReplacementPath);
        Assert.Equal(BoundaryReplacement, await File.ReadAllTextAsync(boundaryReplacementPath!));
        Assert.Empty(Directory.EnumerateFiles(QueueRoot(boundaryPaths), "ledger-*.json"));
    }

    [Fact]
    public async Task Post_publication_substitution_fails_without_deleting_the_previous_generation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstStore = new TriggerQueueStore(paths);
        await TriggerQueueTestData.Service(firstStore).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        var oldGeneration = GenerationPath(QueueRoot(paths), 1);
        string? replacementPath = null;
        var observer = new CallbackObserver(onPublished: (_, destination) =>
        {
            replacementPath = destination;
            File.Move(destination, destination + ".displaced");
            File.WriteAllText(destination, "post-publication-replacement");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));

        Assert.True(File.Exists(oldGeneration));
        Assert.NotNull(replacementPath);
        Assert.Equal("post-publication-replacement", await File.ReadAllTextAsync(replacementPath!));

        using var contentWorkspace = new TestWorkspace();
        var contentPaths = new WorkspacePaths(contentWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(contentPaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        string? mutatedPath = null;
        var contentObserver = new CallbackObserver(onPublished: (_, published) =>
        {
            mutatedPath = published;
            using var stream = new FileStream(published, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var first = stream.ReadByte();
            Assert.NotEqual(-1, first);
            stream.Position = 0;
            stream.WriteByte((byte)(first ^ 1));
            stream.Flush(flushToDisk: true);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(contentPaths, observer: contentObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        Assert.True(File.Exists(GenerationPath(QueueRoot(contentPaths), 1)));
        Assert.NotNull(mutatedPath);
        Assert.True(File.Exists(mutatedPath));
    }

    [Fact]
    public async Task Cleanup_source_claim_and_final_window_substitution_never_delete_replacements()
    {
        using var sourceWorkspace = new TestWorkspace();
        var sourcePaths = new WorkspacePaths(sourceWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(sourcePaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        string? sourceReplacement = null;
        var sourceObserver = new CallbackObserver(onCleanupPrepared: (_, source, _) =>
        {
            sourceReplacement = source;
            File.Move(source, source + ".displaced");
            File.WriteAllText(source, "cleanup-source-replacement");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(sourcePaths, observer: sourceObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        Assert.NotNull(sourceReplacement);
        Assert.Equal("cleanup-source-replacement", await File.ReadAllTextAsync(sourceReplacement!));

        using var claimWorkspace = new TestWorkspace();
        var claimPaths = new WorkspacePaths(claimWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(claimPaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        string? claimReplacement = null;
        var claimObserver = new CallbackObserver(onCleanupClaimed: (_, claim) =>
        {
            claimReplacement = claim;
            File.Delete(claim);
            File.WriteAllText(claim, "cleanup-claim-replacement");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(claimPaths, observer: claimObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        Assert.NotNull(claimReplacement);
        Assert.Equal("cleanup-claim-replacement", await File.ReadAllTextAsync(claimReplacement!));

        using var finalWindowWorkspace = new TestWorkspace();
        var finalWindowPaths = new WorkspacePaths(finalWindowWorkspace.RootPath);
        await TriggerQueueTestData.Service(new TriggerQueueStore(finalWindowPaths)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        string? finalWindowReplacement = null;
        var finalWindowObserver = new CallbackObserver(onCleanupDeleting: (_, claim) =>
        {
            finalWindowReplacement = claim;
            File.Move(claim, claim + ".displaced");
            File.WriteAllText(claim, "cleanup-final-window-replacement");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(finalWindowPaths, observer: finalWindowObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        Assert.NotNull(finalWindowReplacement);
        Assert.Equal("cleanup-final-window-replacement", await File.ReadAllTextAsync(finalWindowReplacement!));
    }

    [Fact]
    public async Task Unix_cleanup_retains_zero_length_authenticated_tombstones_that_restart_ignores()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2")));

        var tombstone = Assert.Single(Directory.EnumerateFiles(QueueRoot(paths), ".tombstone-*.tmp"));
        Assert.Equal(0, new FileInfo(tombstone).Length);
        var snapshot = await new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Single(Directory.EnumerateFiles(QueueRoot(paths), "ledger-*.json"));
    }

    [Fact]
    public async Task Unix_tombstone_quota_is_inspectable_and_structurally_backpressures_without_mutation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = TriggerQueueQuota.Default with { MaxDurabilityTombstones = 2 };
        var store = new TriggerQueueStore(paths, quota);
        var first = TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1", TriggerQueueTestData.Temporal(expiresAtUtc: TriggerQueueTestData.CreatedAtUtc.AddSeconds(10)));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(first));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2")));
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-3", "dedup-3", "loop-3")));
        var root = QueueRoot(paths);
        var ledgerPath = Assert.Single(Directory.EnumerateFiles(root, "ledger-*.json"));
        var ledgerContent = await File.ReadAllBytesAsync(ledgerPath);

        var snapshot = await new TriggerQueueStore(paths, quota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));
        var blockedSweep = await new TriggerQueueStore(paths, quota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(11));
        var replay = await TriggerQueueTestData.Service(new TriggerQueueStore(paths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(first));
        var backpressured = await TriggerQueueTestData.Service(new TriggerQueueStore(paths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-4", "dedup-4", "loop-4")));
        var cancellation = await new TriggerQueueStore(paths, quota).CancelAsync(first.DeliveryId, 1, TriggerQueueTestData.CreatedAtUtc.AddSeconds(4));
        var restarted = await new TriggerQueueStore(paths, quota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));

        Assert.Equal(2, snapshot.DurabilityTombstones);
        Assert.True(snapshot.PersistenceBackpressured);
        Assert.True(blockedSweep.PersistenceBackpressured);
        Assert.Equal(TriggerQueueEntryState.Queued, blockedSweep.Entries.Single(entry => entry.DeliveryId.Equals(first.DeliveryId)).State);
        Assert.Equal(TriggerQueueAdmissionStatus.Replayed, replay.Status);
        Assert.Equal(TriggerQueueAdmissionStatus.Backpressured, backpressured.Status);
        Assert.Equal(TriggerQueueAdmissionReason.DurabilityTombstoneCapacityExceeded, backpressured.Reason);
        Assert.Equal(TriggerQueueCancellationStatus.PersistenceBackpressured, cancellation.Status);
        Assert.Equal(snapshot.Generation, restarted.Generation);
        Assert.Equal(snapshot.DurabilityTombstones, restarted.DurabilityTombstones);
        Assert.Equal(snapshot.Entries.Select(entry => entry.DeliveryId), restarted.Entries.Select(entry => entry.DeliveryId));
        Assert.Equal(ledgerContent, await File.ReadAllBytesAsync(ledgerPath));
        Assert.Equal(2, Directory.EnumerateFiles(root, ".tombstone-*.tmp").Count());
    }

    [Fact]
    public async Task Unix_tombstone_preflight_reserves_two_live_generations_before_staging()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = TriggerQueueQuota.Default with { MaxDurabilityTombstones = 1 };
        await TriggerQueueTestData.Service(new TriggerQueueStore(paths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-1", "dedup-1", "loop-1")));
        var uncertainObserver = new CallbackObserver(onPublished: (_, _) => throw new IOException("retain two live generations"));
        await Assert.ThrowsAsync<IOException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, quota, uncertainObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-2", "dedup-2", "loop-2"))));
        var root = QueueRoot(paths);
        var before = Directory.EnumerateFiles(root, "ledger-*.json").Order(StringComparer.Ordinal).ToDictionary(path => path, File.ReadAllBytes);

        var snapshot = await new TriggerQueueStore(paths, quota).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));
        var result = await TriggerQueueTestData.Service(new TriggerQueueStore(paths, quota)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope("delivery-3", "dedup-3", "loop-3")));
        var after = Directory.EnumerateFiles(root, "ledger-*.json").Order(StringComparer.Ordinal).ToDictionary(path => path, File.ReadAllBytes);

        Assert.Equal(2, before.Count);
        Assert.True(snapshot.PersistenceBackpressured);
        Assert.Equal(0, snapshot.DurabilityTombstones);
        Assert.Equal(TriggerQueueAdmissionStatus.Backpressured, result.Status);
        Assert.Equal(TriggerQueueAdmissionReason.DurabilityTombstoneCapacityExceeded, result.Reason);
        Assert.Equal(before.Keys, after.Keys);
        foreach (var item in before)
        {
            Assert.Equal(item.Value, after[item.Key]);
        }

        Assert.Empty(Directory.EnumerateFiles(root, ".tombstone-*.tmp"));
    }

    [Fact]
    public async Task Queue_root_and_lock_inode_replacement_after_staging_fail_before_publication()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var rootWorkspace = new TestWorkspace();
        var rootPaths = new WorkspacePaths(rootWorkspace.RootPath);
        string? movedRoot = null;
        var rootObserver = new CallbackObserver(onStaged: (_, staging, _) =>
        {
            var root = Path.GetDirectoryName(staging)!;
            movedRoot = root + "-moved";
            Directory.Move(root, movedRoot);
            Directory.CreateSymbolicLink(root, movedRoot);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(rootPaths, observer: rootObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));
        Assert.NotNull(movedRoot);
        Assert.Empty(Directory.EnumerateFiles(movedRoot!, "ledger-*.json"));

        using var lockWorkspace = new TestWorkspace();
        var lockPaths = new WorkspacePaths(lockWorkspace.RootPath);
        string? replacementLock = null;
        var lockObserver = new CallbackObserver(onStaged: (_, staging, _) =>
        {
            replacementLock = Path.Combine(Path.GetDirectoryName(staging)!, ".queue.lock");
            File.Move(replacementLock, replacementLock + ".displaced");
            File.WriteAllText(replacementLock, "replacement-lock");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(lockPaths, observer: lockObserver)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));
        Assert.NotNull(replacementLock);
        Assert.Equal("replacement-lock", await File.ReadAllTextAsync(replacementLock!));
        Assert.Empty(Directory.EnumerateFiles(QueueRoot(lockPaths), "ledger-*.json"));
    }

    [Fact]
    public async Task Queue_root_replacement_at_lock_creation_boundary_cannot_mutate_the_replacement_target()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = QueueRoot(paths);
        var movedRoot = root + "-lock-authority";
        Exception? replacementFailure = null;
        var observer = new CallbackObserver(onMutationDirectoryBound: _ =>
        {
            try
            {
                Directory.Move(root, movedRoot);
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "replacement-sentinel"), "untouched");
            }
            catch (Exception exception)
            {
                replacementFailure = exception;
            }
        });

        var operation = new TriggerQueueStore(paths, observer: observer).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var snapshot = await operation;
                Assert.Empty(snapshot.Entries);
                Assert.IsAssignableFrom<IOException>(replacementFailure);
                Assert.False(Directory.Exists(movedRoot));
            }
            catch (InvalidOperationException)
            {
                Assert.Null(replacementFailure);
                Assert.True(Directory.Exists(movedRoot));
                Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(root, "replacement-sentinel")));
                Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root), path => Path.GetFileName(path) == ".queue.lock");
            }

            return;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Null(replacementFailure);
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(root, "replacement-sentinel")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root), path => Path.GetFileName(path) == ".queue.lock");
    }

    [Fact]
    public async Task Queue_root_replacement_at_staging_creation_boundary_cannot_redirect_the_write()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = QueueRoot(paths);
        var movedRoot = root + "-staging-authority";
        Exception? replacementFailure = null;
        var observer = new CallbackObserver(onStagingDirectoryBound: (_, _, _) =>
        {
            try
            {
                Directory.Move(root, movedRoot);
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "replacement-sentinel"), "untouched");
            }
            catch (Exception exception)
            {
                replacementFailure = exception;
            }
        });

        if (OperatingSystem.IsWindows())
        {
            var admitted = await TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope()));
            Assert.Equal(TriggerQueueAdmissionStatus.Queued, admitted.Status);
            Assert.IsAssignableFrom<IOException>(replacementFailure);
            Assert.False(Directory.Exists(movedRoot));
            return;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));
        Assert.Null(replacementFailure);
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(root, "replacement-sentinel")));
        Assert.Single(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task Unix_queue_root_creation_refuses_a_symlinked_missing_ancestor_without_mutating_its_target()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var outside = Path.Combine(workspace.RootPath, "outside-trigger-target");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(paths.AgentPath);
        File.CreateSymbolicLink(paths.AgentFile("triggers"), outside);

        await Assert.ThrowsAnyAsync<Exception>(() => new TriggerQueueStore(paths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Queue_root_replacement_after_native_directory_binding_never_mutates_the_replacement_tree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        string? replacementSource = null;
        string? replacementDestination = null;
        string? movedRoot = null;
        var observer = new CallbackObserver(onPublishingDirectoryBound: (_, staging, destination) =>
        {
            var root = Path.GetDirectoryName(staging)!;
            movedRoot = root + "-authority";
            Directory.Move(root, movedRoot);
            Directory.CreateDirectory(root);
            replacementSource = Path.Combine(root, Path.GetFileName(staging));
            replacementDestination = Path.Combine(root, Path.GetFileName(destination));
            File.WriteAllText(replacementSource, "replacement-source");
            File.WriteAllText(replacementDestination, "replacement-destination");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerQueueTestData.Service(new TriggerQueueStore(paths, observer: observer)).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope())));

        Assert.NotNull(movedRoot);
        Assert.NotNull(replacementSource);
        Assert.NotNull(replacementDestination);
        Assert.Equal("replacement-source", await File.ReadAllTextAsync(replacementSource!));
        Assert.Equal("replacement-destination", await File.ReadAllTextAsync(replacementDestination!));
        Assert.Single(Directory.EnumerateFiles(movedRoot!, "ledger-*.json"));
    }

    [Fact]
    public async Task Symlink_fifo_and_hard_link_artifacts_fail_closed_before_mutation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var symlinkWorkspace = new TestWorkspace();
        var symlinkPaths = new WorkspacePaths(symlinkWorkspace.RootPath);
        var symlinkRoot = QueueRoot(symlinkPaths);
        Directory.CreateDirectory(symlinkRoot);
        var outside = Path.Combine(symlinkWorkspace.RootPath, "outside.json");
        await File.WriteAllTextAsync(outside, "outside");
        File.CreateSymbolicLink(GenerationPath(symlinkRoot, 1), outside);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(symlinkPaths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        using var fifoWorkspace = new TestWorkspace();
        var fifoPaths = new WorkspacePaths(fifoWorkspace.RootPath);
        var fifoRoot = QueueRoot(fifoPaths);
        Directory.CreateDirectory(fifoRoot);
        Assert.Equal(0, MkFifo(GenerationPath(fifoRoot, 1), Convert.ToUInt32("600", 8)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(fifoPaths).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        using var hardLinkWorkspace = new TestWorkspace();
        var hardLinkPaths = new WorkspacePaths(hardLinkWorkspace.RootPath);
        var hardLinkStore = new TriggerQueueStore(hardLinkPaths);
        await hardLinkStore.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3));
        var lockPath = Path.Combine(QueueRoot(hardLinkPaths), ".queue.lock");
        Assert.Equal(0, Link(lockPath, Path.Combine(QueueRoot(hardLinkPaths), "linked-lock")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hardLinkStore.GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));
    }

    [Fact]
    public async Task Artifact_substitution_with_a_fifo_after_observation_fails_without_blocking_or_mutating_evidence()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new TriggerQueueStore(paths);
        await TriggerQueueTestData.Service(store).AdmitAsync(TriggerQueueTestData.QueueRequest(TriggerQueueTestData.Envelope()));
        var root = QueueRoot(paths);
        var ledger = GenerationPath(root, 1);
        var preserved = Path.Combine(workspace.RootPath, "preserved-ledger.json");
        var original = await File.ReadAllBytesAsync(ledger);
        var observer = new CallbackObserver(onArtifactsObserved: _ =>
        {
            File.Move(ledger, preserved);
            Assert.Equal(0, MkFifo(ledger, Convert.ToUInt32("600", 8)));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new TriggerQueueStore(paths, observer: observer).GetSnapshotAsync(TriggerQueueTestData.CreatedAtUtc.AddSeconds(3)));

        Assert.Equal(original, await File.ReadAllBytesAsync(preserved));
        Assert.True(File.Exists(ledger));
    }

    [Fact]
    public void Public_queue_contract_has_no_provider_actuator_selection_dispatch_or_execution_surface()
    {
        var forbidden = new[] { "Provider", "Actuator", "Select", "Dispatch", "Execute", "Lease", "Worker" };
        var types = new[] { typeof(ITriggerQueueAdmissionPort), typeof(ITriggerQueueMutationPort), typeof(ITriggerQueueQueryPort), typeof(ITriggerQueueCancellationPort), typeof(TriggerQueueStore) };
        foreach (var type in types)
        {
            var names = type.GetMethods().Select(method => method.Name).Concat(type.GetProperties().Select(property => property.Name));
            Assert.DoesNotContain(names, name => forbidden.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static string QueueRoot(WorkspacePaths paths) => paths.AgentFile(Path.Combine("triggers", "queue"));

    private static string GenerationPath(string root, long generation) => Path.Combine(root, $"ledger-{generation:D19}.json");

    private static byte[] MutateLedger(string mutation, string content)
    {
        if (mutation == "invalid-utf8")
        {
            return [0xff];
        }

        if (mutation is "whitespace" or "unknown" or "duplicate" or "unsupported" or "partial")
        {
            var malformed = mutation switch
            {
                "whitespace" => " " + content,
                "unknown" => content.Insert(1, "\"unknown\":0,"),
                "duplicate" => content.Insert(1, "\"generation\":1,"),
                "unsupported" => content[..^2] + "2}",
                _ => content[..^1]
            };
            return Encoding.UTF8.GetBytes(malformed);
        }

        var root = JsonNode.Parse(content)!.AsObject();
        var entry = root["entries"]!.AsArray()[0]!.AsObject();
        switch (mutation)
        {
            case "quota-type":
                root["quota"]!["maxQueuedEntries"] = "32";
                break;
            case "quota-range":
                root["quota"]!["maxDurabilityTombstones"] = 0;
                break;
            case "quota-not-object":
                root["quota"] = new JsonArray();
                break;
            case "entries-not-array":
                root["entries"] = new JsonObject();
                break;
            case "entry-property-missing":
                entry.Remove("admissionReason");
                break;
            case "entry-revision":
                entry["revision"] = 0;
                break;
            case "receiptless-admitted":
                entry["receiptRecordedAtUtc"] = null;
                entry["receiptReplayBindingHash"] = null;
                entry["receiptSchemaVersion"] = null;
                break;
            case "partial-receipt":
                entry["receiptSchemaVersion"] = null;
                break;
            case "invalid-receipt":
                entry["receiptReplayBindingHash"] = new string('0', 64);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static TriggerQueueQuota RaceQuota() => new(1, 4, 128 * 1024, 128 * 1024, 512 * 1024, 1);

    private static async Task<string[]> RunCrossProcessRaceAsync(string workspace, (string Delivery, string Deduplication, string Loop) first, (string Delivery, string Deduplication, string Loop) second)
    {
        var gate = Path.Combine(workspace, "release-trigger-queue-hosts");
        var firstOutput = Path.Combine(workspace, "first-trigger-queue-result");
        var secondOutput = Path.Combine(workspace, "second-trigger-queue-result");
        var firstReady = Path.Combine(workspace, "first-trigger-queue-ready");
        var secondReady = Path.Combine(workspace, "second-trigger-queue-ready");
        using var firstProcess = StartCrossProcessHost(workspace, gate, firstReady, firstOutput, first);
        using var secondProcess = StartCrossProcessHost(workspace, gate, secondReady, secondOutput, second);
        await Task.WhenAll(WaitForPathAsync(firstReady), WaitForPathAsync(secondReady));
        await File.WriteAllTextAsync(gate, "go");
        await Task.WhenAll(firstProcess.WaitForExitAsync(), secondProcess.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        var firstError = await firstProcess.StandardError.ReadToEndAsync();
        var secondError = await secondProcess.StandardError.ReadToEndAsync();
        var firstLog = await firstProcess.StandardOutput.ReadToEndAsync();
        var secondLog = await secondProcess.StandardOutput.ReadToEndAsync();
        Assert.True(firstProcess.ExitCode == 0, firstError + Environment.NewLine + firstLog);
        Assert.True(secondProcess.ExitCode == 0, secondError + Environment.NewLine + secondLog);
        return [await File.ReadAllTextAsync(firstOutput), await File.ReadAllTextAsync(secondOutput)];
    }

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(15), $"Cross-process trigger queue host did not report ready: `{path}`.");
            await Task.Delay(10);
        }
    }

    private static Process StartCrossProcessHost(string workspace, string gate, string ready, string output, (string Delivery, string Deduplication, string Loop) delivery, bool crashAfterStaged = false, bool crashAfterPrecursor = false)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(TriggerQueueStoreTests).Assembly.Location);
        startInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=EmbodySense.Core.Persistence.Tests.Triggers.TriggerQueueStoreTests.Cross_process_queue_admission_host");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrossProcessWorkspace] = workspace;
        startInfo.Environment[CrossProcessGate] = gate;
        startInfo.Environment[CrossProcessReady] = ready;
        startInfo.Environment[CrossProcessOutput] = output;
        startInfo.Environment[CrossProcessDelivery] = delivery.Delivery;
        startInfo.Environment[CrossProcessDeduplication] = delivery.Deduplication;
        startInfo.Environment[CrossProcessLoop] = delivery.Loop;
        if (crashAfterStaged)
        {
            startInfo.Environment[CrossProcessCrashAfterStaged] = "1";
        }

        if (crashAfterPrecursor)
        {
            startInfo.Environment[CrossProcessCrashAfterPrecursor] = "1";
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Cross-process trigger queue test host did not start.");
    }

    private static void TerminateCrossProcessHost()
    {
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);

    private sealed class CallbackObserver(
        Action<string>? onMutationDirectoryBound = null,
        Action<string>? onArtifactsObserved = null,
        Action<long, string, string>? onStagingDirectoryBound = null,
        Action<long, string, string>? onStagingPrecursorCreated = null,
        Action<long, string, string>? onStaged = null,
        Action<long, string, string>? onPublishing = null,
        Action<long, string, string>? onPublishingDirectoryBound = null,
        Action<long, string>? onPublished = null,
        Action<long, string, string>? onCleanupPrepared = null,
        Action<long, string>? onCleanupClaimed = null,
        Action<long, string>? onCleanupDeleting = null) : ITriggerQueueDurabilityObserver
    {
        public void OnMutationDirectoryBound(string queueRoot) => onMutationDirectoryBound?.Invoke(queueRoot);

        public void OnArtifactsObserved(string queueRoot) => onArtifactsObserved?.Invoke(queueRoot);

        public void OnStagingDirectoryBound(long generation, string precursorPath, string destinationPath) => onStagingDirectoryBound?.Invoke(generation, precursorPath, destinationPath);

        public void OnStagingPrecursorCreated(long generation, string precursorPath, string destinationPath) => onStagingPrecursorCreated?.Invoke(generation, precursorPath, destinationPath);

        public void OnStaged(long generation, string stagingPath, string destinationPath) => onStaged?.Invoke(generation, stagingPath, destinationPath);

        public void OnPublishing(long generation, string stagingPath, string destinationPath) => onPublishing?.Invoke(generation, stagingPath, destinationPath);

        public void OnPublishingDirectoryBound(long generation, string stagingPath, string destinationPath) => onPublishingDirectoryBound?.Invoke(generation, stagingPath, destinationPath);

        public void OnPublished(long generation, string destinationPath) => onPublished?.Invoke(generation, destinationPath);

        public void OnCleanupPrepared(long generation, string sourcePath, string claimPath) => onCleanupPrepared?.Invoke(generation, sourcePath, claimPath);

        public void OnCleanupClaimed(long generation, string claimPath) => onCleanupClaimed?.Invoke(generation, claimPath);

        public void OnCleanupDeleting(long generation, string claimPath) => onCleanupDeleting?.Invoke(generation, claimPath);
    }
}

using EmbodySense.Core.Application.Tests.Loops.Posture;
using EmbodySense.Core.Application.Tests.Loops.Sleep;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Tests.Triggers;

public sealed class TriggerQueueLifecycleControlServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_delivery_cancellation_uses_trusted_time_and_preserves_dispatch_ambiguity()
    {
        var entry = DispatchingEntry();
        var port = new StubOperationalTriggerQueuePort
        {
            Snapshot = Snapshot(entry),
            Cancellation = (_, _, cancelledAtUtc) => new TriggerQueueCancellationResult(
                TriggerQueueCancellationStatus.Cancelled,
                Ambiguous(entry, cancelledAtUtc))
        };
        var service = new TriggerQueueLifecycleControlService(port, port, new StubGovernedLoopSleepTimeProvider(_now));

        var result = await service.CancelDeliveryAsync(entry.DeliveryId.Value, entry.Revision);

        Assert.Equal(TriggerQueueDeliveryCancellationStatus.Applied, result.Status);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Equal(_now, Assert.Single(port.Cancellations).CancelledAtUtc);
    }

    [Fact]
    public async Task All_pending_refuses_over_bound_before_mutation_and_reports_partial_progress_truthfully()
    {
        var first = Entry("delivery-1", "deduplication-1");
        var second = Entry("delivery-2", "deduplication-2", revision: 2);
        var overBound = new StubOperationalTriggerQueuePort { Snapshot = Snapshot(first, second) };
        var overBoundService = new TriggerQueueLifecycleControlService(overBound, overBound, new StubGovernedLoopSleepTimeProvider(_now));

        var refused = await overBoundService.CancelPendingForLoopAsync("loop-1", 1);

        Assert.Equal(TriggerQueuePendingCancellationStatus.BoundExceeded, refused.Status);
        Assert.Empty(overBound.Cancellations);

        var partial = new StubOperationalTriggerQueuePort { Snapshot = Snapshot(first, second) };
        var calls = 0;
        partial.Cancellation = (_, _, cancelledAtUtc) => ++calls == 1
            ? new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.Cancelled, Terminal(first, TriggerQueueEntryState.Cancelled, TriggerQueueTerminalReason.Cancelled, cancelledAtUtc))
            : new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.RevisionConflict, second with { Revision = 3 });
        var partialService = new TriggerQueueLifecycleControlService(partial, partial, new StubGovernedLoopSleepTimeProvider(_now));

        var result = await partialService.CancelPendingForLoopAsync("loop-1", 2);

        Assert.Equal(TriggerQueuePendingCancellationStatus.PartiallyApplied, result.Status);
        Assert.Equal(2, result.MatchedCount);
        Assert.Equal(1, result.AppliedCount);
    }

    [Fact]
    public async Task Empty_replay_and_malformed_requests_never_mutate()
    {
        var port = new StubOperationalTriggerQueuePort { Snapshot = Snapshot() };
        var service = new TriggerQueueLifecycleControlService(port, port, new StubGovernedLoopSleepTimeProvider(_now));

        var empty = await service.CancelPendingForLoopAsync("loop-1", 10);
        var invalidLoop = await service.CancelPendingForLoopAsync("NOT VALID", 10);
        var invalidDelivery = await service.CancelDeliveryAsync("NOT VALID", 1);

        Assert.Equal(TriggerQueuePendingCancellationStatus.NoMatches, empty.Status);
        Assert.Equal(TriggerQueuePendingCancellationStatus.Invalid, invalidLoop.Status);
        Assert.Equal(TriggerQueueDeliveryCancellationStatus.Invalid, invalidDelivery.Status);
        Assert.Empty(port.Cancellations);

        var corrupt = new StubOperationalTriggerQueuePort { Snapshot = Snapshot(Entry()) with { QueuedEntries = 2 } };
        var corruptService = new TriggerQueueLifecycleControlService(corrupt, corrupt, new StubGovernedLoopSleepTimeProvider(_now));

        var rejected = await corruptService.CancelPendingForLoopAsync("loop-1", 10);

        Assert.Equal(TriggerQueuePendingCancellationStatus.Unavailable, rejected.Status);
        Assert.Equal("trigger-queue-evidence-invalid", rejected.ReasonCode);
        Assert.Empty(corrupt.Cancellations);
    }

    private static TriggerQueueEntry Entry(string delivery = "delivery-1", string deduplication = "deduplication-1", long revision = 1)
    {
        Assert.True(TriggerDeliveryId.TryParse(delivery, out var deliveryId));
        Assert.True(TriggerDeduplicationId.TryParse(deduplication, out var deduplicationId));
        return new TriggerQueueEntry(
            deliveryId!,
            deduplicationId!,
            "loop-1",
            new string('a', 64),
            1,
            1,
            1,
            TriggerQueueEntryState.Queued,
            TriggerQueueTerminalReason.None,
            new TriggerQueueOrderKey(_now.AddMinutes(-1), TriggerQueuePriority.Normal, _now.AddMinutes(-1), deliveryId!.Value),
            revision,
            _now.AddMinutes(-1),
            null,
            TriggerAdmissionStatus.Admitted,
            TriggerAdmissionReason.EvidenceAccepted);
    }

    private static TriggerQueueEntry Terminal(TriggerQueueEntry entry, TriggerQueueEntryState state, TriggerQueueTerminalReason reason, DateTimeOffset terminalAtUtc)
        => entry with { State = state, TerminalReason = reason, Revision = entry.Revision + 1, QueuedReservationBytes = 0, TerminalAtUtc = terminalAtUtc };

    private static TriggerQueueEntry DispatchingEntry()
    {
        var entry = Entry();
        var lease = new TriggerWorkerLease("worker-1", 1, _now.AddSeconds(-30), _now.AddMinutes(1), 0);
        var dispatch = new TriggerDispatchEvidence(
            TriggerWorkerRequestHash.ComputeOperationId(entry.DeliveryId, lease.Generation),
            new string('b', 64),
            new string('c', 64),
            _now.AddSeconds(-20),
            TriggerDispatchOutcome.IntentRecorded,
            null,
            "intent-recorded");
        return entry with { State = TriggerQueueEntryState.Dispatching, WorkerLease = lease, Dispatch = dispatch };
    }

    private static TriggerQueueEntry Ambiguous(TriggerQueueEntry entry, DateTimeOffset terminalAtUtc)
        => Terminal(entry, TriggerQueueEntryState.NeedsReview, TriggerQueueTerminalReason.AmbiguousDispatch, terminalAtUtc) with
        {
            WorkerLease = entry.WorkerLease! with { ReleasedAtUtc = terminalAtUtc },
            Dispatch = entry.Dispatch! with
            {
                Outcome = TriggerDispatchOutcome.NeedsReview,
                OutcomeRecordedAtUtc = terminalAtUtc,
                Detail = "ambiguous-dispatch"
            }
        };

    private static TriggerQueueSnapshot Snapshot(params TriggerQueueEntry[] entries)
        => new(
            TriggerQueueSnapshot.CurrentSchemaVersion,
            1,
            TriggerQueueQuota.Default,
            entries.Length,
            entries.Sum(entry => (long)entry.SerializedEntryBytes),
            entries.Sum(entry => (long)entry.QueuedReservationBytes),
            entries.Length,
            entries.Sum(entry => (long)entry.SerializedEntryBytes),
            entries.Sum(entry => (long)entry.RetainedReservationBytes),
            0,
            false,
            entries);
}

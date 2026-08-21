using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Tests.Inference.Profiles;

public sealed class GovernedModelUsageReconciliationServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 12, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_dispatch_usage_and_reconciliation_replay_without_double_counting()
    {
        var fixture = Fixture();
        var usage = Usage(4, 5, 2, 9, 60);

        var dispatch = await fixture.Service.RecordDispatchAsync(new GovernedModelDispatchEvidenceRequest(fixture.Identity, fixture.Reservation.ContentHash, true, Hash('d')));
        var observed = await fixture.Service.ObserveUsageAsync(new GovernedModelUsageObservationRequest(fixture.Identity, fixture.Reservation.ContentHash, usage, Hash('e')));
        var reconciled = await fixture.Service.ReconcileAsync(fixture.Identity);
        var count = fixture.Ledger.History.Count;

        var dispatchReplay = await fixture.Service.RecordDispatchAsync(new GovernedModelDispatchEvidenceRequest(fixture.Identity, fixture.Reservation.ContentHash, true, Hash('d')));
        var usageReplay = await fixture.Service.ObserveUsageAsync(new GovernedModelUsageObservationRequest(fixture.Identity, fixture.Reservation.ContentHash, usage, Hash('e')));
        var reconciliationReplay = await fixture.Service.ReconcileAsync(fixture.Identity);

        Assert.Equal(GovernedModelUsageTransitionStatus.Applied, dispatch.Status);
        Assert.Equal(GovernedModelUsageTransitionStatus.Applied, observed.Status);
        Assert.Equal(GovernedModelUsageTransitionStatus.Applied, reconciled.Status);
        Assert.Equal(GovernedModelUsageTransitionStatus.Replayed, dispatchReplay.Status);
        Assert.Equal(GovernedModelUsageTransitionStatus.Replayed, usageReplay.Status);
        Assert.Equal(GovernedModelUsageTransitionStatus.Replayed, reconciliationReplay.Status);
        Assert.Equal(count, fixture.Ledger.History.Count);
        Assert.Equal(4, count);
        Assert.Equal(4, reconciled.Entry?.Used?.InputTokens);
        Assert.Equal(6, reconciled.Entry?.Released?.InputTokens);
        Assert.False(reconciled.Entry?.UsageUnknown);
    }

    [Fact]
    public async Task Conflicting_usage_is_append_only_attention_evidence()
    {
        var fixture = Fixture();
        await fixture.Service.RecordDispatchAsync(new GovernedModelDispatchEvidenceRequest(fixture.Identity, fixture.Reservation.ContentHash, true, Hash('d')));
        await fixture.Service.ObserveUsageAsync(new GovernedModelUsageObservationRequest(fixture.Identity, fixture.Reservation.ContentHash, Usage(1, 1, 1, 2, 10), Hash('e')));

        var conflict = await fixture.Service.ObserveUsageAsync(new GovernedModelUsageObservationRequest(fixture.Identity, fixture.Reservation.ContentHash, Usage(2, 1, 1, 3, 10), Hash('f')));

        Assert.Equal(GovernedModelUsageTransitionStatus.AttentionRequired, conflict.Status);
        Assert.Equal(GovernedModelUsageLedgerPhase.AttentionRequired, fixture.Ledger.History[^1].Phase);
        Assert.Equal(4, fixture.Ledger.History.Count);
        Assert.Equal(Hash('f'), fixture.Ledger.History[^1].EvidenceHash);
    }

    [Fact]
    public async Task Repeated_identical_conflicting_usage_replays_one_attention_disposition()
    {
        var fixture = Fixture();
        await fixture.Service.RecordDispatchAsync(new GovernedModelDispatchEvidenceRequest(fixture.Identity, fixture.Reservation.ContentHash, true, Hash('d')));
        await fixture.Service.ObserveUsageAsync(new GovernedModelUsageObservationRequest(fixture.Identity, fixture.Reservation.ContentHash, Usage(1, 1, 1, 2, 10), Hash('e')));
        var conflictingRequest = new GovernedModelUsageObservationRequest(fixture.Identity, fixture.Reservation.ContentHash, Usage(2, 1, 1, 3, 10), Hash('f'));

        var first = await fixture.Service.ObserveUsageAsync(conflictingRequest);
        var count = fixture.Ledger.History.Count;
        var replay = await fixture.Service.ObserveUsageAsync(conflictingRequest);

        Assert.Equal(GovernedModelUsageTransitionStatus.AttentionRequired, first.Status);
        Assert.Equal(GovernedModelUsageTransitionStatus.AttentionRequired, replay.Status);
        Assert.Equal(count, fixture.Ledger.History.Count);
        Assert.Equal(first.Entry?.ContentHash, replay.Entry?.ContentHash);
    }

    [Fact]
    public async Task Unavailable_usage_never_becomes_zero_or_releases_reservation()
    {
        var fixture = Fixture();
        var unavailable = LlmInferenceUsageEvidence.Unavailable("provider/test", "v1");
        await fixture.Service.RecordDispatchAsync(new GovernedModelDispatchEvidenceRequest(fixture.Identity, fixture.Reservation.ContentHash, true, Hash('d')));
        await fixture.Service.ObserveUsageAsync(new GovernedModelUsageObservationRequest(fixture.Identity, fixture.Reservation.ContentHash, unavailable, Hash('e')));

        var result = await fixture.Service.ReconcileAsync(fixture.Identity);

        Assert.Equal(GovernedModelUsageTransitionStatus.Applied, result.Status);
        Assert.True(result.Entry?.UsageUnknown);
        Assert.Equal(0, result.Entry?.Released?.InputTokens);
        Assert.Equal(0, result.Entry?.Released?.CostMicros);
        Assert.Equal("USD", result.Entry?.Released?.Currency);
    }

    [Fact]
    public async Task Usage_above_reservation_is_retained_without_clipping_and_requires_attention()
    {
        var fixture = Fixture();
        await fixture.Service.RecordDispatchAsync(new GovernedModelDispatchEvidenceRequest(fixture.Identity, fixture.Reservation.ContentHash, true, Hash('d')));
        await fixture.Service.ObserveUsageAsync(new GovernedModelUsageObservationRequest(fixture.Identity, fixture.Reservation.ContentHash, Usage(11, 5, 2, 16, 101), Hash('e')));

        var result = await fixture.Service.ReconcileAsync(fixture.Identity);

        Assert.Equal(GovernedModelUsageTransitionStatus.AttentionRequired, result.Status);
        Assert.Equal(11, result.Entry?.Used?.InputTokens);
        Assert.Equal(101, result.Entry?.Used?.CostMicros);
        Assert.Equal(0, result.Entry?.Released?.InputTokens);
    }

    private static ReconciliationFixture Fixture()
    {
        var identity = GovernedModelUsageLedgerIdentity.Create(1, "workspace-sha256:" + Hash('1'), "run-default", "graph-default", "revision-1", Hash('a'), 1, Hash('d'), Hash('e'), Hash('f'), Hash('7'), "node-inference", 0, 0, 1, "attempt-1", 1, Hash('b'), Hash('c'));
        var reservationCeiling = GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Bounded(10),
            GovernedModelUsageLimit.Bounded(10),
            GovernedModelUsageLimit.Bounded(10),
            GovernedModelUsageLimit.Bounded(20),
            GovernedModelMonetaryLimit.Bounded("USD", 100));
        var reservation = GovernedModelUsageLedgerEntry.Create(1, identity, 1, GovernedModelUsageLedgerPhase.ReservationCommitted, reservationCeiling, null, null, null, false, Hash('b'), null, _now);
        var ledger = new Ledger(reservation);
        return new ReconciliationFixture(new GovernedModelUsageReconciliationService(ledger, new FixedTimeProvider(_now.AddSeconds(1))), ledger, identity, reservation);
    }

    private static LlmInferenceUsageEvidence Usage(long input, long output, long cached, long total, long micros)
        => LlmInferenceUsageEvidence.Create(1, "provider/test", "v1", GovernedModelUsageMeasurement.Authoritative(input), GovernedModelUsageMeasurement.Authoritative(output), GovernedModelUsageMeasurement.Authoritative(cached), GovernedModelUsageMeasurement.Authoritative(total), GovernedModelMonetaryUsageMeasurement.Authoritative("USD", micros));

    private sealed record ReconciliationFixture(GovernedModelUsageReconciliationService Service, Ledger Ledger, GovernedModelUsageLedgerIdentity Identity, GovernedModelUsageLedgerEntry Reservation);

    private sealed class Ledger(GovernedModelUsageLedgerEntry reservation) : IGovernedModelUsageLedger
    {
        internal List<GovernedModelUsageLedgerEntry> History { get; } = [reservation];
        public Task<GovernedModelUsageLedgerReadResult> ReadAsync(GovernedModelUsageLedgerIdentity identity, CancellationToken cancellationToken = default)
            => Task.FromResult(new GovernedModelUsageLedgerReadResult(GovernedModelUsageLedgerReadStatus.Found, History.ToArray(), History.Count));

        public Task<GovernedModelUsageLedgerRunReadResult> ReadRunAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
            => Task.FromResult(new GovernedModelUsageLedgerRunReadResult(GovernedModelUsageLedgerReadStatus.Found, History.ToArray(), History.Count));
        public Task<GovernedModelUsageReservationResult> ReserveAsync(GovernedModelUsageReservationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GovernedModelUsageLedgerAppendResult> AppendAsync(GovernedModelUsageLedgerEntry entry, long expectedGeneration, CancellationToken cancellationToken = default)
        {
            if (expectedGeneration != History.Count || entry.Generation != expectedGeneration + 1)
            {
                return Task.FromResult(new GovernedModelUsageLedgerAppendResult(GovernedModelUsageLedgerAppendStatus.Conflict, History.Count));
            }
            History.Add(entry);
            return Task.FromResult(new GovernedModelUsageLedgerAppendResult(GovernedModelUsageLedgerAppendStatus.Appended, History.Count));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Hash(char value) => new(value, 64);
}

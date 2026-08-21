using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Startup.Inference.Profiles;

namespace EmbodySense.Core.Startup.Tests.Inference.Profiles;

public sealed class ModelUsageRunProjectorTests
{
    private const string WorkspaceId = "workspace-sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string RunId = "run-one";
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");

    [Fact]
    public void Projection_aggregates_only_complete_authoritative_dimensions_and_retains_unknown_reservations_by_currency()
    {
        var complete = ReconciledHistory("node-a", "attempt-one", 0, 1, 1, CompleteUsage(4, "USD", 40), usageUnknown: false);
        var unknown = ReconciledHistory("node-a", "attempt-two", 0, 2, 2, LlmInferenceUsageEvidence.Unavailable("provider-test", "v1"), usageUnknown: true);
        var eurOutstanding = DispatchedHistory("node-b", "attempt-three", 1, 1, 1, "EUR");
        var completeOtherNode = ReconciledHistory("node-c", "attempt-four", 2, 1, 1, CompleteUsage(6, "USD", 20), usageUnknown: false);
        var entries = complete.Concat(unknown).Concat(eurOutstanding).Concat(completeOtherNode).ToArray();
        Assert.True(GovernedModelUsageLedgerHistoryValidator.IsValid(complete, complete[0].Identity, complete.Length));
        Assert.True(GovernedModelUsageLedgerHistoryValidator.IsValid(unknown, unknown[0].Identity, unknown.Length));
        Assert.True(GovernedModelUsageLedgerHistoryValidator.IsValid(eurOutstanding, eurOutstanding[0].Identity, eurOutstanding.Length));
        Assert.True(GovernedModelUsageLedgerHistoryValidator.IsValid(completeOtherNode, completeOtherNode[0].Identity, completeOtherNode.Length));

        var projection = ModelUsageRunProjector.Project(
            new GovernedModelUsageLedgerRunReadResult(GovernedModelUsageLedgerReadStatus.Found, entries, entries.Length),
            WorkspaceId,
            RunId);

        Assert.Equal("Found", projection.Status);
        Assert.Equal(4, projection.Attempts.Count);
        Assert.Equal(["attempt-one", "attempt-two", "attempt-three", "attempt-four"], projection.Attempts.Select(attempt => attempt.AttemptOperationId));
        var run = Assert.IsType<EmbodySense.Core.Startup.Loops.Execution.Models.LoopRunModelUsageAggregateSnapshot>(projection.Run);
        Assert.Equal(4, run.AttemptCount);
        Assert.Equal(2, run.UsageUnavailableAttemptCount);
        Assert.Equal(2, run.UsageUnknownAttemptCount);
        Assert.Equal(2, run.OutstandingReservationAttemptCount);
        Assert.Equal("Unavailable", run.InputTokens.Status);
        Assert.Null(run.InputTokens.AuthoritativeValue);
        Assert.Equal(20, run.InputTokens.OutstandingBoundedReservation);
        Assert.Equal(["EUR", "USD"], run.MonetaryCosts.Select(cost => cost.Currency));
        Assert.All(run.MonetaryCosts, cost => Assert.Equal("Unavailable", cost.Status));
        Assert.Equal(30m, run.MonetaryCosts.Single(cost => cost.Currency == "EUR").OutstandingBoundedReservationMicros);
        Assert.Equal(100m, run.MonetaryCosts.Single(cost => cost.Currency == "USD").OutstandingBoundedReservationMicros);
        Assert.Equal(["node-a", "node-b", "node-c"], projection.NodeSeries.Select(series => series.NodeId));
        Assert.Equal("Unavailable", projection.NodeSeries.Single(series => series.NodeId == "node-a").OutputTokens.Status);
        Assert.Equal("Authoritative", projection.NodeSeries.Single(series => series.NodeId == "node-c").OutputTokens.Status);
        Assert.Equal(0, projection.NodeSeries.Single(series => series.NodeId == "node-c").OutputTokens.AuthoritativeValue);
    }

    [Fact]
    public void Projection_fails_closed_for_cross_run_or_malformed_read_results()
    {
        var entries = ReconciledHistory("node-a", "attempt-one", 0, 1, 1, CompleteUsage(4, "USD", 40), usageUnknown: false);
        var wrongRun = ModelUsageRunProjector.Project(
            new GovernedModelUsageLedgerRunReadResult(GovernedModelUsageLedgerReadStatus.Found, entries, entries.Length),
            WorkspaceId,
            "run-two");
        var contradictory = ModelUsageRunProjector.Project(
            new GovernedModelUsageLedgerRunReadResult(GovernedModelUsageLedgerReadStatus.NotFound, entries, entries.Length),
            WorkspaceId,
            RunId);

        Assert.Equal("Unavailable", wrongRun.Status);
        Assert.Empty(wrongRun.Attempts);
        Assert.Equal("Unavailable", contradictory.Status);
        Assert.Empty(contradictory.Attempts);
    }

    [Fact]
    public void Proved_not_started_attention_does_not_project_provider_ambiguity_or_outstanding_budget()
    {
        var reservation = Reservation("node-a", "attempt-not-started", 0, 1, 1, "USD");
        var notStarted = GovernedModelUsageLedgerEntry.Create(
            1,
            reservation.Identity,
            2,
            GovernedModelUsageLedgerPhase.DispatchProvedNotStarted,
            reservation.Reservation,
            null,
            null,
            null,
            false,
            Hash('2'),
            reservation.ContentHash,
            _now.AddSeconds(1));
        var attention = GovernedModelUsageLedgerEntry.Create(
            1,
            reservation.Identity,
            3,
            GovernedModelUsageLedgerPhase.AttentionRequired,
            reservation.Reservation,
            null,
            null,
            null,
            false,
            Hash('3'),
            notStarted.ContentHash,
            _now.AddSeconds(2));
        GovernedModelUsageLedgerEntry[] entries = [reservation, notStarted, attention];
        Assert.True(GovernedModelUsageLedgerHistoryValidator.IsValid(entries, reservation.Identity, entries.Length));

        var projection = ModelUsageRunProjector.Project(
            new GovernedModelUsageLedgerRunReadResult(GovernedModelUsageLedgerReadStatus.Found, entries, entries.Length),
            WorkspaceId,
            RunId);

        var attempt = Assert.Single(projection.Attempts);
        Assert.Equal(nameof(GovernedModelUsageLedgerPhase.AttentionRequired), attempt.Phase);
        Assert.False(attempt.UsageUnknown);
        Assert.False(attempt.ReservationOutstanding);
        Assert.Equal(0, projection.Run?.OutstandingReservationAttemptCount);
        Assert.Equal(0, projection.Run?.InputTokens.OutstandingBoundedReservation);
    }

    [Fact]
    public void Pre_dispatch_reservation_remains_visibly_outstanding_until_not_started_is_proved()
    {
        var reservation = Reservation("node-a", "attempt-reserved", 0, 1, 1, "USD");

        var projection = ModelUsageRunProjector.Project(
            new GovernedModelUsageLedgerRunReadResult(
                GovernedModelUsageLedgerReadStatus.Found,
                [reservation],
                1),
            WorkspaceId,
            RunId);

        var attempt = Assert.Single(projection.Attempts);
        Assert.Equal(nameof(GovernedModelUsageLedgerPhase.ReservationCommitted), attempt.Phase);
        Assert.False(attempt.UsageUnknown);
        Assert.True(attempt.ReservationOutstanding);
        Assert.Equal(1, projection.Run?.OutstandingReservationAttemptCount);
        Assert.Equal(10, projection.Run?.InputTokens.OutstandingBoundedReservation);
        Assert.Equal(100m, Assert.Single(projection.Run!.MonetaryCosts).OutstandingBoundedReservationMicros);
    }

    private static GovernedModelUsageLedgerEntry[] ReconciledHistory(
        string nodeId,
        string operationId,
        int planOrdinal,
        int visitOrdinal,
        int attemptNumber,
        LlmInferenceUsageEvidence usage,
        bool usageUnknown)
    {
        var reservation = Reservation(nodeId, operationId, planOrdinal, visitOrdinal, attemptNumber, "USD");
        var dispatch = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 2, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, reservation.Reservation, null, null, null, true, Hash('e'), reservation.ContentHash, _now.AddSeconds(1));
        var observed = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 3, GovernedModelUsageLedgerPhase.UsageObserved, reservation.Reservation, usage, null, null, usageUnknown, Hash('f'), dispatch.ContentHash, _now.AddSeconds(2));
        var used = GovernedModelUsageVector.Create(
            Authoritative(usage.InputTokens),
            Authoritative(usage.OutputTokens),
            Authoritative(usage.CachedTokens),
            Authoritative(usage.TotalTokens),
            usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative ? usage.MonetaryCost.Currency : null,
            usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative ? usage.MonetaryCost.Micros : 0);
        var released = GovernedModelUsageVector.Create(
            usage.InputTokens.Status == GovernedModelUsageEvidenceStatus.Authoritative ? 10 - usage.InputTokens.Value : 0,
            0,
            0,
            0,
            "USD",
            usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative ? 100 - usage.MonetaryCost.Micros : 0);
        var reconciled = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 4, GovernedModelUsageLedgerPhase.Reconciled, reservation.Reservation, usage, used, released, usageUnknown, observed.ContentHash, observed.ContentHash, _now.AddSeconds(3));
        return [reservation, dispatch, observed, reconciled];
    }

    private static GovernedModelUsageLedgerEntry[] DispatchedHistory(string nodeId, string operationId, int planOrdinal, int visitOrdinal, int attemptNumber, string currency)
    {
        var reservation = Reservation(nodeId, operationId, planOrdinal, visitOrdinal, attemptNumber, currency);
        var dispatch = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 2, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, reservation.Reservation, null, null, null, true, Hash('e'), reservation.ContentHash, _now.AddSeconds(1));
        return [reservation, dispatch];
    }

    private static GovernedModelUsageLedgerEntry Reservation(string nodeId, string operationId, int planOrdinal, int visitOrdinal, int attemptNumber, string currency)
    {
        var identity = GovernedModelUsageLedgerIdentity.Create(
            1,
            WorkspaceId,
            RunId,
            "graph-one",
            "revision-one",
            Hash('a'),
            1,
            Hash('b'),
            Hash('c'),
            Hash('d'),
            Hash('e'),
            nodeId,
            planOrdinal,
            planOrdinal,
            visitOrdinal,
            operationId,
            attemptNumber,
            Hash('f'),
            Hash('0'));
        var ceiling = GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Bounded(10),
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelMonetaryLimit.Bounded(currency, currency == "USD" ? 100 : 30));
        return GovernedModelUsageLedgerEntry.Create(1, identity, 1, GovernedModelUsageLedgerPhase.ReservationCommitted, ceiling, null, null, null, false, Hash('1'), null, _now);
    }

    private static LlmInferenceUsageEvidence CompleteUsage(long input, string currency, long micros)
        => LlmInferenceUsageEvidence.Create(
            1,
            "provider-test",
            "v1",
            GovernedModelUsageMeasurement.Authoritative(input),
            GovernedModelUsageMeasurement.Authoritative(0),
            GovernedModelUsageMeasurement.Authoritative(0),
            GovernedModelUsageMeasurement.Authoritative(input),
            GovernedModelMonetaryUsageMeasurement.Authoritative(currency, micros));

    private static long Authoritative(GovernedModelUsageMeasurement measurement)
        => measurement.Status == GovernedModelUsageEvidenceStatus.Authoritative ? measurement.Value : 0;

    private static string Hash(char value) => new(value, 64);
}

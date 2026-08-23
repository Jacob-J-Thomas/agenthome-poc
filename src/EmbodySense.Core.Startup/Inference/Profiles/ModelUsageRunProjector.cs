using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Creates a bounded, surface-neutral run usage projection from authenticated ledger evidence.</summary>
public static class ModelUsageRunProjector
{
    /// <summary>Validates and projects one exact workspace/run read, failing closed on contradictory or forged input.</summary>
    public static LoopRunModelUsageSnapshot Project(
        GovernedModelUsageLedgerRunReadResult? read,
        string workspaceId,
        string runId)
    {
        if (!IsValidRead(read, workspaceId, runId))
        {
            return Empty("Unavailable", read?.WorkspaceGeneration ?? 0);
        }
        if (read!.Status == GovernedModelUsageLedgerReadStatus.NotFound)
        {
            return Empty("NotFound", read.WorkspaceGeneration);
        }
        if (read.Status == GovernedModelUsageLedgerReadStatus.Unavailable)
        {
            return Empty("Unavailable", read.WorkspaceGeneration);
        }

        try
        {
            var histories = read.Entries
                .GroupBy(entry => entry.Identity.ContentHash, StringComparer.Ordinal)
                .Select(group => group.OrderBy(entry => entry.Generation).ToArray())
                .OrderBy(history => history[0].Identity.NodeId, StringComparer.Ordinal)
                .ThenBy(history => history[0].Identity.ActivationOrdinal)
                .ThenBy(history => history[0].Identity.VisitOrdinal)
                .ThenBy(history => history[0].Identity.AttemptNumber)
                .ThenBy(history => history[0].Identity.AttemptOperationId, StringComparer.Ordinal)
                .ToArray();
            if (histories.Length == 0 || histories.Any(history => !GovernedModelUsageLedgerHistoryValidator.IsValid(history, history[0].Identity, history.Length)))
            {
                return Empty("Unavailable", read.WorkspaceGeneration);
            }

            var attempts = histories.Select(ProjectAttempt).ToArray();
            var runAggregate = Aggregate("Run", null, histories);
            var nodeSeries = histories
                .GroupBy(history => history[0].Identity.NodeId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => Aggregate("NodeSeries", group.Key, group.ToArray()))
                .ToArray();
            return new LoopRunModelUsageSnapshot(
                "Found",
                read.WorkspaceGeneration,
                Array.AsReadOnly(attempts),
                runAggregate,
                Array.AsReadOnly(nodeSeries));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return Empty("Unavailable", read.WorkspaceGeneration);
        }
    }

    private static LoopRunModelUsageAttemptSnapshot ProjectAttempt(IReadOnlyList<GovernedModelUsageLedgerEntry> history)
    {
        var reservation = history[0];
        var latest = history[^1];
        var usage = history.LastOrDefault(entry => entry.Usage is not null)?.Usage;
        var used = history.LastOrDefault(entry => entry.Used is not null)?.Used;
        var released = history.LastOrDefault(entry => entry.Released is not null)?.Released;
        var identity = reservation.Identity;
        return new LoopRunModelUsageAttemptSnapshot(
            identity.NodeId,
            identity.PlanOrdinal,
            identity.ActivationOrdinal,
            identity.VisitOrdinal,
            identity.AttemptOperationId,
            identity.AttemptNumber,
            identity.ProfilePinHash,
            identity.BudgetPolicyHash,
            latest.Phase.ToString(),
            latest.Generation,
            reservation.ContentHash,
            latest.ContentHash,
            reservation.Reservation!,
            usage,
            used,
            released,
            latest.UsageUnknown,
            IsReservationOutstanding(history));
    }

    private static LoopRunModelUsageAggregateSnapshot Aggregate(
        string scope,
        string? nodeId,
        IReadOnlyList<GovernedModelUsageLedgerEntry[]> histories)
    {
        var attempts = histories.Select(history => ProjectAttempt(history)).ToArray();
        return new LoopRunModelUsageAggregateSnapshot(
            scope,
            nodeId,
            attempts.Length,
            attempts.Count(attempt => HasUnavailableUsage(attempt.Usage)),
            attempts.Count(attempt => attempt.UsageUnknown),
            attempts.Count(attempt => attempt.ReservationOutstanding),
            AggregateDimension(attempts, usage => usage.InputTokens, reservation => reservation.InputTokens),
            AggregateDimension(attempts, usage => usage.OutputTokens, reservation => reservation.OutputTokens),
            AggregateDimension(attempts, usage => usage.CachedTokens, reservation => reservation.CachedTokens),
            AggregateDimension(attempts, usage => usage.TotalTokens, reservation => reservation.TotalTokens),
            AggregateMoney(attempts));
    }

    private static LoopRunModelUsageDimensionAggregateSnapshot AggregateDimension(
        IReadOnlyList<LoopRunModelUsageAttemptSnapshot> attempts,
        Func<LlmInferenceUsageEvidence, GovernedModelUsageMeasurement> selectUsage,
        Func<GovernedModelUsageCeiling, GovernedModelUsageLimit> selectReservation)
    {
        var complete = attempts.Count > 0 && attempts.All(attempt => attempt.Usage is not null && selectUsage(attempt.Usage).Status == GovernedModelUsageEvidenceStatus.Authoritative);
        long? authoritative = null;
        if (complete)
        {
            authoritative = attempts.Aggregate(0L, (sum, attempt) => checked(sum + selectUsage(attempt.Usage!).Value));
        }
        var outstanding = attempts
            .Where(attempt => IsDimensionReservationOutstanding(attempt, attempt.Usage is null ? null : selectUsage(attempt.Usage)))
            .Select(attempt => selectReservation(attempt.Reservation))
            .Where(limit => limit.IsBounded)
            .Aggregate(0L, (sum, limit) => checked(sum + limit.Maximum));
        return new LoopRunModelUsageDimensionAggregateSnapshot(
            complete ? "Authoritative" : "Unavailable",
            authoritative,
            outstanding);
    }

    private static IReadOnlyList<LoopRunModelMonetaryCurrencyAggregateSnapshot> AggregateMoney(
        IReadOnlyList<LoopRunModelUsageAttemptSnapshot> attempts)
    {
        var authoritative = attempts
            .Where(attempt => attempt.Usage?.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative)
            .Select(attempt => attempt.Usage!.MonetaryCost)
            .ToArray();
        var currencies = authoritative.Select(value => value.Currency!)
            .Concat(attempts.Where(attempt => attempt.ReservationOutstanding && attempt.Reservation.MonetaryCost.IsBounded)
                .Select(attempt => attempt.Reservation.MonetaryCost.Currency!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var complete = attempts.Count > 0
            && authoritative.Length == attempts.Count
            && authoritative.Select(value => value.Currency).Distinct(StringComparer.Ordinal).Count() == 1;
        return Array.AsReadOnly(currencies.Select(currency =>
        {
            decimal? authoritativeMicros = complete && string.Equals(authoritative[0].Currency, currency, StringComparison.Ordinal)
                ? authoritative.Aggregate(0m, (sum, value) => sum + value.Micros)
                : null;
            var outstanding = attempts
                .Where(attempt => attempt.ReservationOutstanding
                    && attempt.Reservation.MonetaryCost.IsBounded
                    && IsDimensionReservationOutstanding(attempt, attempt.Usage?.MonetaryCost)
                    && string.Equals(attempt.Reservation.MonetaryCost.Currency, currency, StringComparison.Ordinal))
                .Aggregate(0m, (sum, attempt) => sum + attempt.Reservation.MonetaryCost.MaximumMicros);
            return new LoopRunModelMonetaryCurrencyAggregateSnapshot(
                currency,
                authoritativeMicros is not null ? "Authoritative" : "Unavailable",
                authoritativeMicros,
                outstanding);
        }).ToArray());
    }

    private static bool IsReservationOutstanding(IReadOnlyList<GovernedModelUsageLedgerEntry> history)
    {
        if (history.Any(entry => entry.Phase == GovernedModelUsageLedgerPhase.DispatchProvedNotStarted))
        {
            return false;
        }
        if (!history.Any(entry => entry.Phase == GovernedModelUsageLedgerPhase.DispatchBoundaryReached))
        {
            return true;
        }
        var latest = history[^1];
        return !history.Any(entry => entry.Phase == GovernedModelUsageLedgerPhase.Reconciled)
            || latest.UsageUnknown;
    }

    private static bool IsDimensionReservationOutstanding(
        LoopRunModelUsageAttemptSnapshot attempt,
        GovernedModelUsageMeasurement? measurement)
        => attempt.ReservationOutstanding
            && (!string.Equals(attempt.Phase, nameof(GovernedModelUsageLedgerPhase.Reconciled), StringComparison.Ordinal)
                || measurement?.Status != GovernedModelUsageEvidenceStatus.Authoritative);

    private static bool IsDimensionReservationOutstanding(
        LoopRunModelUsageAttemptSnapshot attempt,
        GovernedModelMonetaryUsageMeasurement? measurement)
        => attempt.ReservationOutstanding
            && (!string.Equals(attempt.Phase, nameof(GovernedModelUsageLedgerPhase.Reconciled), StringComparison.Ordinal)
                || measurement?.Status != GovernedModelUsageEvidenceStatus.Authoritative);

    private static bool HasUnavailableUsage(LlmInferenceUsageEvidence? usage)
        => usage is null
            || usage.InputTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || usage.OutputTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || usage.CachedTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || usage.TotalTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Unavailable;

    private static bool IsValidRead(GovernedModelUsageLedgerRunReadResult? read, string workspaceId, string runId)
    {
        if (read is null
            || !Enum.IsDefined(read.Status)
            || read.WorkspaceGeneration < 0
            || read.Entries is null
            || read.Entries.Count > GovernedModelContractLimits.MaxWorkspaceUsageLedgerEntries
            || read.Status == GovernedModelUsageLedgerReadStatus.Found != (read.Entries.Count > 0)
            || read.Status != GovernedModelUsageLedgerReadStatus.Found && read.Entries.Count != 0)
        {
            return false;
        }
        var operations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in read.Entries)
        {
            if (!GovernedModelContractValidator.IsValid(entry)
                || !string.Equals(entry.Identity.WorkspaceId, workspaceId, StringComparison.Ordinal)
                || !string.Equals(entry.Identity.RunId, runId, StringComparison.Ordinal)
                || operations.TryGetValue(entry.Identity.AttemptOperationId, out var hash) && !string.Equals(hash, entry.Identity.ContentHash, StringComparison.Ordinal))
            {
                return false;
            }
            operations[entry.Identity.AttemptOperationId] = entry.Identity.ContentHash;
        }
        return true;
    }

    private static LoopRunModelUsageSnapshot Empty(string status, long generation)
        => new(status, generation, [], null, []);
}

using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Common.Inference.Profiles;

/// <summary>Validates the complete append-only state machine for one exact provider-attempt usage history.</summary>
public static class GovernedModelUsageLedgerHistoryValidator
{
    /// <summary>
    /// Validates a bounded complete history, including identity, hash chain, timestamps, legal transitions,
    /// immutable evidence, and conservative usage reconciliation.
    /// </summary>
    public static bool IsValid(
        IReadOnlyList<GovernedModelUsageLedgerEntry>? entries,
        GovernedModelUsageLedgerIdentity? expectedIdentity,
        long expectedGeneration)
    {
        try
        {
            if (!GovernedModelContractValidator.IsValid(expectedIdentity)
                || entries is null
                || entries.Count is < 1 or > GovernedModelContractLimits.MaxUsageLedgerEntries
                || expectedGeneration != entries.Count)
            {
                return false;
            }

            var reservation = entries[0];
            if (!GovernedModelContractValidator.IsValid(reservation)
                || reservation.Phase != GovernedModelUsageLedgerPhase.ReservationCommitted
                || reservation.Generation != 1
                || reservation.PreviousEntryHash is not null
                || reservation.Reservation is null
                || !string.Equals(reservation.Identity.ContentHash, expectedIdentity!.ContentHash, StringComparison.Ordinal))
            {
                return false;
            }

            for (var index = 1; index < entries.Count; index++)
            {
                var previous = entries[index - 1];
                var current = entries[index];
                if (!GovernedModelContractValidator.IsValid(current)
                    || current.Generation != index + 1
                    || !string.Equals(current.Identity.ContentHash, expectedIdentity.ContentHash, StringComparison.Ordinal)
                    || !string.Equals(current.PreviousEntryHash, previous.ContentHash, StringComparison.Ordinal)
                    || current.RecordedAtUtc < previous.RecordedAtUtc
                    || current.Reservation is null
                    || !string.Equals(current.Reservation.ContentHash, reservation.Reservation.ContentHash, StringComparison.Ordinal)
                    || !IsLegalTransition(previous.Phase, current.Phase)
                    || !EvidenceIsImmutable(previous, current)
                    || !TransitionEvidenceIsConsistent(previous, current)
                    || !PhaseEvidenceIsConsistent(current))
                {
                    return false;
                }
            }

            return PhaseEvidenceIsConsistent(reservation);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLegalTransition(GovernedModelUsageLedgerPhase previous, GovernedModelUsageLedgerPhase current)
        => previous switch
        {
            GovernedModelUsageLedgerPhase.ReservationCommitted => current is GovernedModelUsageLedgerPhase.DispatchProvedNotStarted or GovernedModelUsageLedgerPhase.DispatchBoundaryReached,
            GovernedModelUsageLedgerPhase.DispatchProvedNotStarted => current == GovernedModelUsageLedgerPhase.AttentionRequired,
            GovernedModelUsageLedgerPhase.DispatchBoundaryReached => current is GovernedModelUsageLedgerPhase.UsageObserved or GovernedModelUsageLedgerPhase.AttentionRequired,
            GovernedModelUsageLedgerPhase.UsageObserved => current is GovernedModelUsageLedgerPhase.Reconciled or GovernedModelUsageLedgerPhase.AttentionRequired,
            GovernedModelUsageLedgerPhase.Reconciled => current == GovernedModelUsageLedgerPhase.AttentionRequired,
            _ => false
        };

    private static bool TransitionEvidenceIsConsistent(GovernedModelUsageLedgerEntry previous, GovernedModelUsageLedgerEntry current)
    {
        if (current.Phase != GovernedModelUsageLedgerPhase.AttentionRequired)
        {
            return true;
        }

        return previous.Phase switch
        {
            GovernedModelUsageLedgerPhase.DispatchProvedNotStarted => current.Usage is null
                && current.Used is null
                && current.Released is null
                && !current.UsageUnknown,
            GovernedModelUsageLedgerPhase.DispatchBoundaryReached => current.Usage is null
                && current.Used is null
                && current.Released is null
                && current.UsageUnknown,
            GovernedModelUsageLedgerPhase.UsageObserved => current.Usage is not null
                && string.Equals(current.Usage.ContentHash, previous.Usage?.ContentHash, StringComparison.Ordinal)
                && current.UsageUnknown,
            GovernedModelUsageLedgerPhase.Reconciled => current.Usage is not null
                && current.Used is not null
                && current.Released is not null
                && string.Equals(current.Usage.ContentHash, previous.Usage?.ContentHash, StringComparison.Ordinal)
                && string.Equals(current.Used.ContentHash, previous.Used?.ContentHash, StringComparison.Ordinal)
                && string.Equals(current.Released.ContentHash, previous.Released?.ContentHash, StringComparison.Ordinal)
                && current.UsageUnknown == previous.UsageUnknown,
            _ => false
        };
    }

    private static bool EvidenceIsImmutable(GovernedModelUsageLedgerEntry previous, GovernedModelUsageLedgerEntry current)
    {
        if (previous.Usage is not null
            && (current.Usage is null || !string.Equals(current.Usage.ContentHash, previous.Usage.ContentHash, StringComparison.Ordinal)))
        {
            return false;
        }

        if (previous.Used is not null
            && (current.Used is null || !string.Equals(current.Used.ContentHash, previous.Used.ContentHash, StringComparison.Ordinal)))
        {
            return false;
        }

        return previous.Released is null
            || current.Released is not null && string.Equals(current.Released.ContentHash, previous.Released.ContentHash, StringComparison.Ordinal);
    }

    private static bool PhaseEvidenceIsConsistent(GovernedModelUsageLedgerEntry entry)
        => entry.Phase switch
        {
            GovernedModelUsageLedgerPhase.ReservationCommitted => entry.Usage is null
                && entry.Used is null
                && entry.Released is null
                && !entry.UsageUnknown,
            GovernedModelUsageLedgerPhase.DispatchProvedNotStarted => entry.Usage is null
                && entry.Used is null
                && entry.Released is null
                && !entry.UsageUnknown,
            GovernedModelUsageLedgerPhase.DispatchBoundaryReached => entry.Usage is null
                && entry.Used is null
                && entry.Released is null
                && entry.UsageUnknown,
            GovernedModelUsageLedgerPhase.UsageObserved => entry.Usage is not null
                && entry.Used is null
                && entry.Released is null
                && entry.UsageUnknown == HasUnknown(entry.Usage, entry.Reservation!),
            GovernedModelUsageLedgerPhase.Reconciled => IsMathematicallyReconciled(entry, mustBeWithinReservation: true),
            GovernedModelUsageLedgerPhase.AttentionRequired => IsValidAttention(entry),
            _ => false
        };

    private static bool IsValidAttention(GovernedModelUsageLedgerEntry entry)
    {
        if (entry.Usage is null)
        {
            return entry.Used is null && entry.Released is null;
        }

        if ((entry.Used is null) != (entry.Released is null))
        {
            return false;
        }

        return entry.Used is null
            ? entry.UsageUnknown
            : IsMathematicallyReconciled(entry, mustBeWithinReservation: false);
    }

    private static bool IsMathematicallyReconciled(GovernedModelUsageLedgerEntry entry, bool mustBeWithinReservation)
    {
        if (entry.Reservation is null || entry.Usage is null || entry.Used is null || entry.Released is null)
        {
            return false;
        }

        var reservation = entry.Reservation;
        var usage = entry.Usage;
        var used = entry.Used;
        var released = entry.Released;
        var expectedUsed = CreateUsed(usage);
        var expectedReleased = CreateReleased(reservation, expectedUsed, usage);
        var exceeds = ExceedsReservation(expectedUsed, reservation, usage);
        return string.Equals(used.ContentHash, expectedUsed.ContentHash, StringComparison.Ordinal)
            && string.Equals(released.ContentHash, expectedReleased.ContentHash, StringComparison.Ordinal)
            && entry.UsageUnknown == (HasUnknown(usage, reservation) || exceeds)
            && (!mustBeWithinReservation || !exceeds);
    }

    private static GovernedModelUsageVector CreateUsed(LlmInferenceUsageEvidence usage)
        => GovernedModelUsageVector.Create(
            AuthoritativeValue(usage.InputTokens),
            AuthoritativeValue(usage.OutputTokens),
            AuthoritativeValue(usage.CachedTokens),
            AuthoritativeValue(usage.TotalTokens),
            usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative ? usage.MonetaryCost.Currency : null,
            usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative ? usage.MonetaryCost.Micros : 0);

    private static GovernedModelUsageVector CreateReleased(GovernedModelUsageCeiling reservation, GovernedModelUsageVector used, LlmInferenceUsageEvidence usage)
        => GovernedModelUsageVector.Create(
            ProvedUnused(reservation.InputTokens, used.InputTokens, usage.InputTokens.Status),
            ProvedUnused(reservation.OutputTokens, used.OutputTokens, usage.OutputTokens.Status),
            ProvedUnused(reservation.CachedTokens, used.CachedTokens, usage.CachedTokens.Status),
            ProvedUnused(reservation.TotalTokens, used.TotalTokens, usage.TotalTokens.Status),
            reservation.MonetaryCost.Currency,
            reservation.MonetaryCost.IsBounded && usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative
                ? Math.Max(0, reservation.MonetaryCost.MaximumMicros - used.CostMicros)
                : 0);

    private static bool ExceedsReservation(GovernedModelUsageVector used, GovernedModelUsageCeiling reservation, LlmInferenceUsageEvidence usage)
        => reservation.InputTokens.IsBounded && used.InputTokens > reservation.InputTokens.Maximum
            || reservation.OutputTokens.IsBounded && used.OutputTokens > reservation.OutputTokens.Maximum
            || reservation.CachedTokens.IsBounded && used.CachedTokens > reservation.CachedTokens.Maximum
            || reservation.TotalTokens.IsBounded && used.TotalTokens > reservation.TotalTokens.Maximum
            || reservation.MonetaryCost.IsBounded
                && usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative
                && (used.CostMicros > reservation.MonetaryCost.MaximumMicros
                || !string.Equals(used.Currency, reservation.MonetaryCost.Currency, StringComparison.Ordinal));

    private static bool HasUnknown(LlmInferenceUsageEvidence usage, GovernedModelUsageCeiling reservation)
        => reservation.InputTokens.IsBounded && usage.InputTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.OutputTokens.IsBounded && usage.OutputTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.CachedTokens.IsBounded && usage.CachedTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.TotalTokens.IsBounded && usage.TotalTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.MonetaryCost.IsBounded && usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Unavailable;

    private static long AuthoritativeValue(GovernedModelUsageMeasurement measurement)
        => measurement.Status == GovernedModelUsageEvidenceStatus.Authoritative ? measurement.Value : 0;

    private static long ProvedUnused(GovernedModelUsageLimit reservation, long used, GovernedModelUsageEvidenceStatus status)
        => reservation.IsBounded && status == GovernedModelUsageEvidenceStatus.Authoritative ? Math.Max(0, reservation.Maximum - used) : 0;
}

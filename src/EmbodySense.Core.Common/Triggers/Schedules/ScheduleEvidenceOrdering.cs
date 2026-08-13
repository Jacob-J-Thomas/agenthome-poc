using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

internal static class ScheduleEvidenceOrdering
{
    internal static int Compare(ScheduleOccurrenceDispositionEvidence? left, ScheduleOccurrenceDispositionEvidence? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var comparison = left.FirstOrdinal.CompareTo(right.FirstOrdinal);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.LastOrdinal.CompareTo(right.LastOrdinal);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.FirstScheduledLocal.Ticks.CompareTo(right.FirstScheduledLocal.Ticks);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Nullable.Compare(left.FirstScheduledAtUtc?.UtcTicks, right.FirstScheduledAtUtc?.UtcTicks);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = DispositionPhase(left.Disposition).CompareTo(DispositionPhase(right.Disposition));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.RecordedAtUtc.UtcTicks.CompareTo(right.RecordedAtUtc.UtcTicks);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Disposition.CompareTo(right.Disposition);
        if (comparison != 0)
        {
            return comparison;
        }

        return string.Compare(left.ReasonCode, right.ReasonCode, StringComparison.Ordinal);
    }

    private static int DispositionPhase(ScheduleOccurrenceDisposition disposition)
        => disposition == ScheduleOccurrenceDisposition.OverlapDeferred ? 0 : 1;
}

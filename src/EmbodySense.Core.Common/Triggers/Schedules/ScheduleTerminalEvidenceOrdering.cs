using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

internal static class ScheduleTerminalEvidenceOrdering
{
    internal static int Compare(ScheduleTerminalDeliveryEvidence? left, ScheduleTerminalDeliveryEvidence? right)
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

        var comparison = (left.Occurrence?.Ordinal ?? long.MinValue).CompareTo(right.Occurrence?.Ordinal ?? long.MinValue);
        return comparison != 0
            ? comparison
            : string.Compare(left.Identity?.OccurrenceId?.Value, right.Identity?.OccurrenceId?.Value, StringComparison.Ordinal);
    }
}

using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Compares deterministic queue ordering inputs without selecting or dispatching an entry.</summary>
public static class TriggerQueueOrdering
{
    /// <summary>Compares eligibility, bounded priority, acceptance time, and stable delivery ordinal in that order.</summary>
    /// <param name="left">The left ordering input.</param>
    /// <param name="right">The right ordering input.</param>
    /// <returns>A negative value when <paramref name="left"/> precedes <paramref name="right"/>, zero when equal, or a positive value otherwise.</returns>
    public static int Compare(TriggerQueueOrderKey left, TriggerQueueOrderKey right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var comparison = left.EligibleAtUtc.CompareTo(right.EligibleAtUtc);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.Priority.CompareTo(left.Priority);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.AcceptedAtUtc.CompareTo(right.AcceptedAtUtc);
        return comparison != 0 ? comparison : string.Compare(left.DeliveryId, right.DeliveryId, StringComparison.Ordinal);
    }
}

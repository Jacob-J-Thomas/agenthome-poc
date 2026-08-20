namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines a durable queue-admission result retained with a pending delivery.</summary>
public enum ScheduleDeliveryResultKind
{
    /// <summary>The result is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The delivery was newly queued.</summary>
    Queued = 1,
    /// <summary>The exact durable delivery outcome was replayed.</summary>
    Replayed = 2,
    /// <summary>The delivery was conclusively rejected.</summary>
    Rejected = 3,
    /// <summary>A bounded queue limit rejected the delivery without finalizing the occurrence.</summary>
    Backpressured = 4,
    /// <summary>Current queue evidence was unavailable.</summary>
    Unavailable = 5,
    /// <summary>The external outcome is ambiguous and must not be guessed.</summary>
    Ambiguous = 6,
}

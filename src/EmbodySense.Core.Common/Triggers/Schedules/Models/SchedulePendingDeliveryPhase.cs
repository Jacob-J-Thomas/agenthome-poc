namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines the exact durable stage of one pending schedule occurrence.</summary>
public enum SchedulePendingDeliveryPhase
{
    /// <summary>The phase is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The occurrence and optimistic claim are durable before any fresh external reads.</summary>
    Claimed = 1,
    /// <summary>Fresh evidence, recurrence proof, successor plan, and exact envelope are durable before queue admission.</summary>
    Prepared = 2,
    /// <summary>The exact queue-admission result is durable and awaits atomic finalization.</summary>
    ResultObserved = 3,
}

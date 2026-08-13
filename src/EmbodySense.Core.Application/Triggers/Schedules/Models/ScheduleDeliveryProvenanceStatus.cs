namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Defines closed schedule-delivery provenance lookup outcomes.</summary>
public enum ScheduleDeliveryProvenanceStatus
{
    /// <summary>The store returned no recognized outcome.</summary>
    Unknown = 0,
    /// <summary>One exact, durably accepted occurrence was found.</summary>
    Found = 1,
    /// <summary>No retained occurrence shares either supplied deterministic identity.</summary>
    NotFound = 2,
    /// <summary>Retained evidence shares an identity but conflicts with the complete envelope.</summary>
    Conflict = 3,
    /// <summary>The store could not be reached.</summary>
    Unavailable = 4,
    /// <summary>Persisted schedule data failed validation.</summary>
    Corrupt = 5,
    /// <summary>The bounded store refused the lookup under load.</summary>
    Backpressured = 6,
    /// <summary>More than one retained accepted occurrence matched the supplied envelope.</summary>
    Ambiguous = 7,
    /// <summary>An exact prepared delivery is durable, but accepted terminal evidence is not finalized yet.</summary>
    PendingFinalization = 8,
}

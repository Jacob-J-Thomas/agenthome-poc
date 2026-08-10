namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies a durable lifecycle mutation outcome.</summary>
public enum CapabilityLifecycleMutationStatus
{
    /// <summary>The exact transition was durably applied.</summary>
    Applied = 1,
    /// <summary>The exact terminal operation was replayed.</summary>
    Replayed = 2,
    /// <summary>A revision, dependency set, preview, or operation identity changed.</summary>
    Conflict = 3,
    /// <summary>A required dependent blocked the transition.</summary>
    Blocked = 4,
    /// <summary>The capability or rollback target does not exist.</summary>
    NotFound = 5,
    /// <summary>The request violates the closed lifecycle contract.</summary>
    Invalid = 6,
    /// <summary>No safe proved outcome could be established.</summary>
    Unavailable = 7,
    /// <summary>The exact preview was durably retired without applying its transition.</summary>
    Discarded = 8
}

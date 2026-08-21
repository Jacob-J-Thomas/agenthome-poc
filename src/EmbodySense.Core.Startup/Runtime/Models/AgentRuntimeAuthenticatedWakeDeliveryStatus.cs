namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Classifies one authenticated-event wake delivery through the shared governed runtime.</summary>
public enum AgentRuntimeAuthenticatedWakeDeliveryStatus
{
    /// <summary>The exact continuation is conclusively committed.</summary>
    Committed = 1,
    /// <summary>The exact wake was already terminal and no continuation was repeated.</summary>
    Duplicate = 2,
    /// <summary>The checkpoint has not reached an eligible event delivery.</summary>
    NotEligible = 3,
    /// <summary>Another exact wake already claimed this checkpoint.</summary>
    Late = 4,
    /// <summary>The checkpoint no longer names the current execution posture.</summary>
    Stale = 5,
    /// <summary>An optimistic or immutable-identity conflict prevented safe progress.</summary>
    Conflict = 6,
    /// <summary>The exact run was cancelled.</summary>
    Cancelled = 7,
    /// <summary>The exact run expired or already terminated.</summary>
    Expired = 8,
    /// <summary>The exact run is paused.</summary>
    Paused = 9,
    /// <summary>Unattended continuation is unauthorized or explicit review is required.</summary>
    ReviewBlocked = 10,
    /// <summary>The continuation outcome requires explicit reconciliation.</summary>
    AmbiguousAttempt = 11,
    /// <summary>The exact continuation failed conclusively.</summary>
    Failed = 12,
    /// <summary>The request or adapter result was malformed.</summary>
    Invalid = 13,
    /// <summary>The checkpoint, wake, or execution could not be found.</summary>
    NotFound = 14,
    /// <summary>A required dependency was conclusively unavailable.</summary>
    Unavailable = 15,
}

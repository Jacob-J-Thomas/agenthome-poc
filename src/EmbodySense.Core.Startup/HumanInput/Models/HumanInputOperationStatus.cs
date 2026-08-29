namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Identifies one normalized privacy-safe Human Input operation result.</summary>
public enum HumanInputOperationStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The operation committed durably.</summary>
    Committed = 1,
    /// <summary>The same exact durable operation was replayed.</summary>
    Replayed = 2,
    /// <summary>The operation shape or untrusted response value was malformed.</summary>
    Invalid = 3,
    /// <summary>Exact optimistic state, immutable intent, or operation identity conflicted.</summary>
    Conflict = 4,
    /// <summary>The exact target request or response does not exist.</summary>
    NotFound = 5,
    /// <summary>The authenticated actor was denied or ineligible.</summary>
    Denied = 6,
    /// <summary>The trusted response window had passed.</summary>
    Late = 7,
    /// <summary>A finite schema-1 limit was exhausted.</summary>
    LimitExceeded = 8,
    /// <summary>A server-owned dependency or configured authority provider was unavailable.</summary>
    Unavailable = 9,
    /// <summary>Available evidence could not establish one safe result.</summary>
    Ambiguous = 10,
}

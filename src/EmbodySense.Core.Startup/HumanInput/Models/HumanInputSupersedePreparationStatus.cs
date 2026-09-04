namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Identifies one bounded Human Input lifecycle-candidate preparation outcome.</summary>
public enum HumanInputSupersedePreparationStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>An opaque candidate key was prepared.</summary>
    Ready = 1,
    /// <summary>The proposal was malformed.</summary>
    Invalid = 2,
    /// <summary>The target request was not found.</summary>
    NotFound = 3,
    /// <summary>The optimistic target state conflicted.</summary>
    Conflict = 4,
    /// <summary>The server-owned actor was denied.</summary>
    Denied = 5,
    /// <summary>The canonical dependency or registry was unavailable.</summary>
    Unavailable = 6,
    /// <summary>Available evidence could not establish one candidate.</summary>
    Ambiguous = 7,
    /// <summary>A finite candidate or lifecycle limit was reached.</summary>
    LimitExceeded = 8
}

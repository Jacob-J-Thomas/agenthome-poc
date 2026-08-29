namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Identifies one bounded Human Input posture-page result.</summary>
public enum HumanInputRequestPosturePageStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The page is a stable projection from one canonical ledger generation.</summary>
    Ready = 1,
    /// <summary>The page size or opaque cursor was malformed.</summary>
    Invalid = 2,
    /// <summary>The cursor is no longer bound to the current canonical ledger generation.</summary>
    Stale = 3,
    /// <summary>The canonical ledger is unavailable.</summary>
    Unavailable = 4,
    /// <summary>Available evidence cannot establish one safe page.</summary>
    Ambiguous = 5,
}

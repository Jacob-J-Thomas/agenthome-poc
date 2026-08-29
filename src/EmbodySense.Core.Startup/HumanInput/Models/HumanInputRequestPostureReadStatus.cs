namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Identifies one exact Human Input posture-read result.</summary>
public enum HumanInputRequestPostureReadStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact redacted posture was read.</summary>
    Ready = 1,
    /// <summary>The exact request identifier was malformed.</summary>
    Invalid = 2,
    /// <summary>The exact request does not exist.</summary>
    NotFound = 3,
    /// <summary>The canonical ledger is unavailable.</summary>
    Unavailable = 4,
    /// <summary>Available evidence cannot establish one safe posture.</summary>
    Ambiguous = 5,
}

namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Identifies one closed durable operational-evidence read outcome.</summary>
public enum GovernedLoopOperationalEvidenceReadStatus
{
    /// <summary>At least one validated item was found.</summary>
    Found = 1,

    /// <summary>The validated catalog exists but contains no items.</summary>
    Empty = 2,

    /// <summary>Bounded persistence capacity prevented a complete read.</summary>
    Backpressured = 3,

    /// <summary>Retained evidence was malformed, inconsistent, or corrupt.</summary>
    Corrupt = 4,

    /// <summary>The durable evidence source was unavailable.</summary>
    Unavailable = 5
}

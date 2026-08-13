namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Identifies one closed background-work enumeration outcome.</summary>
public enum GovernedLoopBackgroundWorkReadStatus
{
    /// <summary>At least one validated candidate was found.</summary>
    Found = 1,

    /// <summary>No candidate was eligible at the observation instant.</summary>
    Empty = 2,

    /// <summary>Bounded persistence capacity prevented a safe enumeration.</summary>
    Backpressured = 3,

    /// <summary>At least one retained catalog was malformed or corrupt.</summary>
    Corrupt = 4,

    /// <summary>At least one required durable catalog was unavailable.</summary>
    Unavailable = 5
}

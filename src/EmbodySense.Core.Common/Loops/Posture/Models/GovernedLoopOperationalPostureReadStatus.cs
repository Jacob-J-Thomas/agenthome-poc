namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Identifies the aggregate trust posture of one bounded operational snapshot.</summary>
public enum GovernedLoopOperationalPostureReadStatus
{
    /// <summary>Every authoritative source returned valid evidence.</summary>
    Available = 1,

    /// <summary>Valid evidence reports at least one finite capacity gate.</summary>
    Backpressured = 2,

    /// <summary>At least one authoritative source returned corrupt evidence.</summary>
    Corrupt = 3,

    /// <summary>At least one authoritative source was unavailable.</summary>
    Unavailable = 4,

    /// <summary>The bounded request was invalid.</summary>
    Invalid = 5
}

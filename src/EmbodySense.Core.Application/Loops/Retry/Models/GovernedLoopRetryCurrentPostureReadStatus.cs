namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Identifies the result of reading exact current retry posture.</summary>
public enum GovernedLoopRetryCurrentPostureReadStatus
{
    /// <summary>The current posture is unavailable.</summary>
    Unavailable = 1,
    /// <summary>The exact current posture was found.</summary>
    Found,
    /// <summary>Current evidence conflicts with the admitted run.</summary>
    Conflict,
}

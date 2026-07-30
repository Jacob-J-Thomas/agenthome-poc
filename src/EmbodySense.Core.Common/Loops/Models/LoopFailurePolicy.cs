namespace EmbodySense.Core.Common.Loops.Models;

/// <summary>
/// Identifies the supported loop failure policy values.
/// </summary>
public enum LoopFailurePolicy
{
    /// <summary>
    /// Identifies the unknown loop failure policy.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the record failure and surface to user loop failure policy.
    /// </summary>
    RecordFailureAndSurfaceToUser
}

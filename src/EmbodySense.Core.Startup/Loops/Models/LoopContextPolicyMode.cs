namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Identifies the supported loop context policy mode values.
/// </summary>
public enum LoopContextPolicyMode
{
    /// <summary>
    /// No supported policy mode was selected.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// The node uses its definition-level default policy.
    /// </summary>
    Inherit = 1,
    /// <summary>
    /// The node supplies an explicit context policy.
    /// </summary>
    Custom = 2
}

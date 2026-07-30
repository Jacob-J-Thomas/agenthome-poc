namespace EmbodySense.Core.Common.Loops.Models;

/// <summary>
/// Identifies the supported loop trigger values.
/// </summary>
public enum LoopTrigger
{
    /// <summary>
    /// Identifies the unknown loop trigger.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the human message loop trigger.
    /// </summary>
    HumanMessage,
    /// <summary>
    /// Identifies the manual loop trigger.
    /// </summary>
    Manual
}

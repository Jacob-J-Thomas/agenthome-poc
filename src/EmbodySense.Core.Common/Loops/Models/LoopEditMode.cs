namespace EmbodySense.Core.Common.Loops.Models;

/// <summary>
/// Identifies the supported loop edit mode values.
/// </summary>
public enum LoopEditMode
{
    /// <summary>
    /// Identifies the unknown loop edit mode.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the system locked loop edit mode.
    /// </summary>
    SystemLocked,
    /// <summary>
    /// Identifies the user editable loop edit mode.
    /// </summary>
    UserEditable,
    /// <summary>
    /// Identifies the agent editable loop edit mode.
    /// </summary>
    AgentEditable
}

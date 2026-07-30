namespace EmbodySense.Core.Common.Loops.Models;

/// <summary>
/// Identifies the supported loop graph node edit mode values.
/// </summary>
public enum LoopGraphNodeEditMode
{
    /// <summary>
    /// Identifies the unknown loop graph node edit mode.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the system locked loop graph node edit mode.
    /// </summary>
    SystemLocked,
    /// <summary>
    /// Identifies the user editable loop graph node edit mode.
    /// </summary>
    UserEditable,
    /// <summary>
    /// Identifies the agent editable loop graph node edit mode.
    /// </summary>
    AgentEditable
}

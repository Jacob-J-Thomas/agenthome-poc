namespace EmbodySense.Core.Application.Loops.Execution.Models;

/// <summary>
/// Identifies the supported default conversation loop turn status values.
/// </summary>
public enum DefaultConversationLoopTurnStatus
{
    /// <summary>
    /// Identifies the unknown default conversation loop turn status.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the completed default conversation loop turn status.
    /// </summary>
    Completed,
    /// <summary>
    /// Identifies the failed default conversation loop turn status.
    /// </summary>
    Failed,
    /// <summary>
    /// Identifies the cancelled default conversation loop turn status.
    /// </summary>
    Cancelled
}

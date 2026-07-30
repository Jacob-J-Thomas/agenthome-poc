namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop operation audit mark status values.
/// </summary>
public enum CustomLoopOperationAuditMarkStatus
{
    /// <summary>
    /// Identifies the marked custom loop operation audit mark status.
    /// </summary>
    Marked = 1,
    /// <summary>
    /// Identifies the already marked custom loop operation audit mark status.
    /// </summary>
    AlreadyMarked = 2,
    /// <summary>
    /// Identifies the not found custom loop operation audit mark status.
    /// </summary>
    NotFound = 3
}

namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Identifies the supported custom loop trace deletion audit mark status values.
/// </summary>
public enum CustomLoopTraceDeletionAuditMarkStatus
{
    /// <summary>
    /// Identifies the marked custom loop trace deletion audit mark status.
    /// </summary>
    Marked = 1,
    /// <summary>
    /// Identifies the already marked custom loop trace deletion audit mark status.
    /// </summary>
    AlreadyMarked = 2,
    /// <summary>
    /// Identifies the not found custom loop trace deletion audit mark status.
    /// </summary>
    NotFound = 3
}

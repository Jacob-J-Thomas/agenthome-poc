namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop operation integrity values.
/// </summary>
public enum CustomLoopOperationIntegrity
{
    /// <summary>
    /// Identifies the not tracked custom loop operation integrity.
    /// </summary>
    NotTracked = 1,
    /// <summary>
    /// Identifies the pending mutation custom loop operation integrity.
    /// </summary>
    PendingMutation = 2,
    /// <summary>
    /// Identifies the pending outcome audit custom loop operation integrity.
    /// </summary>
    PendingOutcomeAudit = 3,
    /// <summary>
    /// Identifies the complete custom loop operation integrity.
    /// </summary>
    Complete = 4
}

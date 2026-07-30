namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Identifies the supported custom loop tool evidence phase values.
/// </summary>
public enum CustomLoopToolEvidencePhase
{
    /// <summary>
    /// Identifies the unknown custom loop tool evidence phase.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the request reserved custom loop tool evidence phase.
    /// </summary>
    RequestReserved = 1,
    /// <summary>
    /// Identifies the governance decided custom loop tool evidence phase.
    /// </summary>
    GovernanceDecided = 2,
    /// <summary>
    /// Identifies the outcome observed custom loop tool evidence phase.
    /// </summary>
    OutcomeObserved = 3,
    /// <summary>
    /// Identifies the integrity failed custom loop tool evidence phase.
    /// </summary>
    IntegrityFailed = 4
}

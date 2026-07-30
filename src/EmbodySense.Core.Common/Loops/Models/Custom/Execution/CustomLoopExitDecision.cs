namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Identifies the supported custom loop exit decision values.
/// </summary>
public enum CustomLoopExitDecision
{
    /// <summary>
    /// Identifies the unknown custom loop exit decision.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the complete custom loop exit decision.
    /// </summary>
    Complete = 1,
    /// <summary>
    /// Identifies the repeat custom loop exit decision.
    /// </summary>
    Repeat = 2,
    /// <summary>
    /// Identifies the invalid custom loop exit decision.
    /// </summary>
    Invalid = 3
}

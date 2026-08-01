namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Identifies how a projected system-loop graph relates to its current executor.
/// </summary>
public enum SystemLoopExecutionSemantics
{
    /// <summary>
    /// Identifies an unknown execution relationship.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies graph structure that is validated as the contract for a dedicated runner rather than dispatched node-by-node by a generic graph executor.
    /// </summary>
    ValidatedRunnerContract
}

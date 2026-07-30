namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop invocation outcome values.
/// </summary>
public enum CustomLoopInvocationOutcome
{
    /// <summary>
    /// Identifies the unknown custom loop invocation outcome.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the workspace execution busy custom loop invocation outcome.
    /// </summary>
    WorkspaceExecutionBusy = 1,
    /// <summary>
    /// Identifies the admitted custom loop invocation outcome.
    /// </summary>
    Admitted = 2,
    /// <summary>
    /// Identifies the rejected custom loop invocation outcome.
    /// </summary>
    Rejected = 3
}

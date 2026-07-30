namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Identifies the supported loop custom tool authority ceiling values.
/// </summary>
public enum LoopCustomToolAuthorityCeiling
{
    /// <summary>
    /// No supported authority ceiling was selected.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Custom loops may be assigned only implemented read-only workspace commands.
    /// </summary>
    WorkspaceReadOnly = 1
}

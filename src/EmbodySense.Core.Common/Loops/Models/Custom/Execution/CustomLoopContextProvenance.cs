namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Identifies the supported custom loop context provenance values.
/// </summary>
public enum CustomLoopContextProvenance
{
    /// <summary>
    /// Identifies the unknown custom loop context provenance.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the harness runtime custom loop context provenance.
    /// </summary>
    HarnessRuntime = 1,
    /// <summary>
    /// Identifies the workspace role file custom loop context provenance.
    /// </summary>
    WorkspaceRoleFile = 2,
    /// <summary>
    /// Identifies the workspace context file custom loop context provenance.
    /// </summary>
    WorkspaceContextFile = 3,
    /// <summary>
    /// Identifies the server run state custom loop context provenance.
    /// </summary>
    ServerRunState = 4,
    /// <summary>
    /// Identifies the authored definition custom loop context provenance.
    /// </summary>
    AuthoredDefinition = 5,
    /// <summary>
    /// Identifies the manual invocation custom loop context provenance.
    /// </summary>
    ManualInvocation = 6,
    /// <summary>
    /// Identifies the logical conversation custom loop context provenance.
    /// </summary>
    LogicalConversation = 7,
    /// <summary>
    /// Identifies the model output custom loop context provenance.
    /// </summary>
    ModelOutput = 8,
    /// <summary>
    /// Identifies the workspace agent identity file custom loop context provenance.
    /// </summary>
    WorkspaceAgentIdentityFile = 9
}

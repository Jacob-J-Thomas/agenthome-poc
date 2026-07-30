namespace EmbodySense.Core.Common.Governance.Tools.Models;

/// <summary>
/// Identifies the supported tool execution outcome values.
/// </summary>
public enum ToolExecutionOutcome
{
    /// <summary>
    /// Identifies the succeeded tool execution outcome.
    /// </summary>
    Succeeded,
    /// <summary>
    /// Identifies the denied tool execution outcome.
    /// </summary>
    Denied,
    /// <summary>
    /// Identifies the approval rejected tool execution outcome.
    /// </summary>
    ApprovalRejected,
    /// <summary>
    /// Identifies the failed tool execution outcome.
    /// </summary>
    Failed
}

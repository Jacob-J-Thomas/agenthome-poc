namespace EmbodySense.Core.Common.Governance.Tools.Models;

/// <summary>
/// Identifies the supported tool approval decision values.
/// </summary>
public enum ToolApprovalDecision
{
    /// <summary>
    /// Identifies the unknown tool approval decision.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the not evaluated tool approval decision.
    /// </summary>
    NotEvaluated = 1,
    /// <summary>
    /// Identifies the not required tool approval decision.
    /// </summary>
    NotRequired = 2,
    /// <summary>
    /// Identifies the approved tool approval decision.
    /// </summary>
    Approved = 3,
    /// <summary>
    /// Identifies the rejected tool approval decision.
    /// </summary>
    Rejected = 4,
    /// <summary>
    /// Identifies the requested tool approval decision.
    /// </summary>
    Requested = 5
}

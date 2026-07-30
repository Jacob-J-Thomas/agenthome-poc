using EmbodySense.Core.Common.Governance.Tools.Models;
namespace EmbodySense.Core.Common.Governance.Tools;

/// <summary>
/// Records a human approval decision together with the deciding identity and explanation.
/// </summary>
/// <param name="Approved">Whether execution was approved.</param>
/// <param name="DecisionBy">The identity that made the decision.</param>
/// <param name="Detail">The human-readable decision explanation.</param>
public sealed record ToolApprovalResponse(bool Approved, string DecisionBy, string Detail)
{
    /// <summary>
    /// Creates an approved response.
    /// </summary>
    /// <param name="decisionBy">The decision by.</param>
    /// <param name="detail">The detail.</param>
    /// <returns>The tool approval response.</returns>
    public static ToolApprovalResponse Approve(string decisionBy, string detail) => new(true, decisionBy, detail);

    /// <summary>
    /// Creates a rejected response.
    /// </summary>
    /// <param name="decisionBy">The decision by.</param>
    /// <param name="detail">The detail.</param>
    /// <returns>The tool approval response.</returns>
    public static ToolApprovalResponse Reject(string decisionBy, string detail) => new(false, decisionBy, detail);
}

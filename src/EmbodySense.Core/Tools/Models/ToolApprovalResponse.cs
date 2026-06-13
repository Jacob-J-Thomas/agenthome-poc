namespace EmbodySense.Core.Tools.Models;

public sealed record ToolApprovalResponse(bool Approved, string DecisionBy, string Detail)
{
    public static ToolApprovalResponse Approve(string decisionBy, string detail) => new(true, decisionBy, detail);

    public static ToolApprovalResponse Reject(string decisionBy, string detail) => new(false, decisionBy, detail);
}

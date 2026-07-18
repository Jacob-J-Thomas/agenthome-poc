namespace EmbodySense.Core.Common.Governance.Tools.Models;

public sealed record ToolResult(
    ToolExecutionOutcome Outcome,
    string OutputText,
    string RequestId,
    string ResolvedPath,
    ToolRequest Request,
    ToolGovernanceEvidence? Governance = null)
{
    public bool Succeeded => Outcome == ToolExecutionOutcome.Succeeded;
}

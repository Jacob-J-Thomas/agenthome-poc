namespace EmbodySense.Core.Tools.Models;

public sealed record ToolResult(
    ToolExecutionOutcome Outcome,
    string OutputText,
    string RequestId,
    string ResolvedPath,
    ToolRequest Request)
{
    public bool Succeeded => Outcome == ToolExecutionOutcome.Succeeded;
}

using EmbodySense.Core.Common.Governance.Tools.Models;
namespace EmbodySense.Core.Common.Governance.Tools;

/// <summary>
/// Records the exact request, governed outcome, resolved path, model-visible output, and optional authority/retention evidence for one tool invocation.
/// </summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="OutputText">The output text.</param>
/// <param name="RequestId">The request ID.</param>
/// <param name="ResolvedPath">The resolved path.</param>
/// <param name="Request">The request.</param>
/// <param name="Governance">The governance.</param>
/// <param name="Retention">The retention.</param>
public sealed record ToolResult(
    ToolExecutionOutcome Outcome,
    string OutputText,
    string RequestId,
    string ResolvedPath,
    ToolRequest Request,
    ToolGovernanceEvidence? Governance = null,
    ToolResultRetentionReference? Retention = null)
{
    /// <summary>
    /// Gets a value indicating whether execution completed successfully.
    /// </summary>
    /// <value><see langword="true"/> only when <see cref="Outcome"/> is <see cref="ToolExecutionOutcome.Succeeded"/>; otherwise, <see langword="false"/>.</value>
    public bool Succeeded => Outcome == ToolExecutionOutcome.Succeeded;
}

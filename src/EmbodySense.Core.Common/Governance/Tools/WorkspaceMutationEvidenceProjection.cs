using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;

namespace EmbodySense.Core.Common.Governance.Tools;

/// <summary>
/// Projects governed workspace mutations into value-free, workspace-relative durable evidence.
/// </summary>
public static class WorkspaceMutationEvidenceProjection
{
    /// <summary>Returns whether the command carries governed workspace mutation semantics.</summary>
    public static bool IsMutation(ToolCommand command)
        => command is ToolCommand.Append or ToolCommand.Write or ToolCommand.Delete;

    /// <summary>
    /// Removes semantic mutation arguments before a request is retained or returned as evidence.
    /// </summary>
    public static ToolRequest ProjectRequest(ToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsMutation(request.Command)
            ? request with { TargetPath = SafeTargetReference(request.TargetPath), Content = null, Pattern = null }
            : request;
    }

    /// <summary>
    /// Replaces an absolute resolved mutation target with its exact workspace-relative target reference.
    /// </summary>
    public static string ProjectResolvedTarget(ToolRequest request, string resolvedTarget)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolvedTarget);
        return IsMutation(request.Command) ? SafeTargetReference(request.TargetPath) : resolvedTarget;
    }

    /// <summary>
    /// Removes path-bearing free text from mutation governance evidence while preserving its decisions and hashes.
    /// </summary>
    public static ToolGovernanceEvidence? ProjectGovernance(ToolRequest request, ToolGovernanceEvidence? governance)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsMutation(request.Command) || governance is null)
        {
            return governance;
        }

        return governance with
        {
            AuthorityDetail = $"Governed workspace mutation authority decision: {governance.AuthorityDecision.ToString().ToLowerInvariant()}.",
            PermissionMatchedPath = null,
            PermissionDetail = governance.PermissionDecision is null
                ? null
                : $"Governed workspace mutation permission decision: {governance.PermissionDecision.Value.ToString().ToLowerInvariant()}.",
            ApprovalDetail = $"Governed workspace mutation approval decision: {governance.ApprovalDecision.ToString().ToLowerInvariant()}.",
        };
    }

    /// <summary>
    /// Produces a fixed semantic-free model and retention message for a mutation outcome.
    /// </summary>
    public static string ProjectOutput(ToolRequest request, ToolExecutionOutcome outcome, string output)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        if (!IsMutation(request.Command))
        {
            return output;
        }

        return outcome switch
        {
            ToolExecutionOutcome.Succeeded => "governed workspace mutation succeeded",
            ToolExecutionOutcome.Denied => "denied: governed workspace mutation authority did not permit execution",
            ToolExecutionOutcome.ApprovalRejected => "rejected: governed workspace mutation approval was declined",
            ToolExecutionOutcome.Failed => "failed: governed workspace mutation did not complete",
            _ => "governed workspace mutation stopped without a recognized outcome",
        };
    }

    /// <summary>Projects an entire mutation result before durable retention or evidence observation.</summary>
    public static ToolResult ProjectResult(ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!IsMutation(result.Request.Command))
        {
            return result;
        }

        return result with
        {
            OutputText = ProjectOutput(result.Request, result.Outcome, result.OutputText),
            ResolvedPath = ProjectResolvedTarget(result.Request, result.ResolvedPath),
            Request = ProjectRequest(result.Request),
            Governance = ProjectGovernance(result.Request, result.Governance),
        };
    }

    private static string SafeTargetReference(string targetPath)
        => WorkspaceRelativeFileTarget.TryParse(targetPath, out var target, out _)
            ? target!.Value
            : "workspace-target-" + WorkspaceActionFingerprint.Compute("embodysense.workspace-mutation-evidence-target.v1", targetPath);
}

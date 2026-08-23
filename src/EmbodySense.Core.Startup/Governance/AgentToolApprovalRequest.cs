using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Startup.Governance;

/// <summary>
/// Provides an interface-layer projection of a governed tool approval request without exposing
/// application-layer permission or tool-request types.
/// </summary>
/// <param name="RequestId">The correlation identifier assigned to the governed request.</param>
/// <param name="Command">The normalized lowercase command name presented for approval.</param>
/// <param name="TargetPath">The caller-supplied target path.</param>
/// <param name="ResolvedPath">The canonical path evaluated by the permission policy.</param>
/// <param name="Operation">The normalized lowercase permission operation.</param>
/// <param name="MatchedPath">The permission rule path that matched, or <c>(default policy)</c> when no explicit rule matched.</param>
/// <param name="Reason">The permission evaluation detail explaining why approval is required.</param>
public sealed record AgentToolApprovalRequest(
    string RequestId,
    string Command,
    string TargetPath,
    string ResolvedPath,
    string Operation,
    string MatchedPath,
    string Reason)
{
    /// <summary>Creates a bounded interface projection, replacing mutation paths and free-form policy text with safe references.</summary>
    public static AgentToolApprovalRequest FromToolApprovalRequest(ToolApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mutation = WorkspaceMutationEvidenceProjection.IsMutation(request.ToolRequest.Command);
        var projected = WorkspaceMutationEvidenceProjection.ProjectRequest(request.ToolRequest);
        return new AgentToolApprovalRequest(
            request.RequestId,
            request.ToolRequest.Command.ToString().ToLowerInvariant(),
            projected.TargetPath,
            WorkspaceMutationEvidenceProjection.ProjectResolvedTarget(request.ToolRequest, request.ResolvedPath),
            request.Operation.ToString().ToLowerInvariant(),
            mutation
                ? "(protected workspace policy)"
                : string.IsNullOrWhiteSpace(request.PermissionEvaluation.MatchedPath) ? "(default policy)" : request.PermissionEvaluation.MatchedPath,
            mutation
                ? $"Governed workspace mutation permission decision: {request.PermissionEvaluation.Decision.ToString().ToLowerInvariant()}."
                : request.PermissionEvaluation.Detail);
    }
}

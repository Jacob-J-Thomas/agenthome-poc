using EmbodySense.Core.Application.Governance.Tools.Models;

namespace EmbodySense.Core.Startup.Governance;

public sealed record AgentToolApprovalRequest(
    string RequestId,
    string Command,
    string TargetPath,
    string ResolvedPath,
    string Operation,
    string MatchedPath,
    string Reason)
{
    internal static AgentToolApprovalRequest FromToolApprovalRequest(ToolApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AgentToolApprovalRequest(
            request.RequestId,
            request.ToolRequest.Command.ToString().ToLowerInvariant(),
            request.ToolRequest.TargetPath,
            request.ResolvedPath,
            request.Operation.ToString().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(request.PermissionEvaluation.MatchedPath) ? "(default policy)" : request.PermissionEvaluation.MatchedPath,
            request.PermissionEvaluation.Detail);
    }
}

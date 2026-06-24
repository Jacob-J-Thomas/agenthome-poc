using EmbodySense.Core.Application.Governance.Tools.Models;

namespace EmbodySense.Web.Models;

public sealed record WebPendingApproval(
    string RequestId,
    long Sequence,
    DateTimeOffset CreatedAtUtc,
    string Command,
    string TargetPath,
    string ResolvedPath,
    string Operation,
    string MatchedPath,
    string Reason)
{
    public static WebPendingApproval FromRequest(ToolApprovalRequest request, long sequence, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new WebPendingApproval(
            request.RequestId,
            sequence,
            createdAtUtc,
            request.ToolRequest.Command.ToString().ToLowerInvariant(),
            request.ToolRequest.TargetPath,
            request.ResolvedPath,
            request.Operation.ToString().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(request.PermissionEvaluation.MatchedPath) ? "(default policy)" : request.PermissionEvaluation.MatchedPath,
            request.PermissionEvaluation.Detail);
    }
}

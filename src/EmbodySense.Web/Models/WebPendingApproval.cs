using EmbodySense.Core.Startup.Governance;

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
    public static WebPendingApproval FromRequest(AgentToolApprovalRequest request, long sequence, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new WebPendingApproval(
            request.RequestId,
            sequence,
            createdAtUtc,
            request.Command,
            request.TargetPath,
            request.ResolvedPath,
            request.Operation,
            request.MatchedPath,
            request.Reason);
    }
}

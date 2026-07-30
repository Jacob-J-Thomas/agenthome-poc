using EmbodySense.Web.Models;
using EmbodySense.Core.Startup.Governance;

namespace EmbodySense.Web;

/// <summary>
/// Projects one connection-owned governed tool request to the approving browser.
/// </summary>
/// <param name="RequestId">The unique request identity used for a decision.</param>
/// <param name="Sequence">The server-assigned ordering sequence.</param>
/// <param name="CreatedAtUtc">The server timestamp at which the request became pending.</param>
/// <param name="Command">The governed command name.</param>
/// <param name="TargetPath">The caller-supplied path text.</param>
/// <param name="ResolvedPath">The canonical path evaluated by permission policy.</param>
/// <param name="Operation">The requested governed operation.</param>
/// <param name="MatchedPath">The permission-rule path that matched the request.</param>
/// <param name="Reason">The audit-facing reason approval is required.</param>
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
    /// <summary>
    /// Creates a browser-safe projection from a governed approval request.
    /// </summary>
    /// <param name="request">The server-owned governed request.</param>
    /// <param name="sequence">The monotonic pending-order sequence.</param>
    /// <param name="createdAtUtc">The server creation timestamp.</param>
    /// <returns>A complete pending-approval projection.</returns>
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

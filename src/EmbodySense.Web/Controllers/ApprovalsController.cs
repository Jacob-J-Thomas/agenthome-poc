using EmbodySense.Web;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>
/// Exposes the local-session HTTP projection of governed tool approvals.
/// </summary>
/// <remarks>
/// Pending approvals are owned by authenticated SignalR connection identifiers. Because HTTP requests
/// do not carry that ownership identity, this controller neither exposes nor completes connection-owned
/// approvals; browser decisions flow through <see cref="Hubs.WebSessionHub"/>.
/// </remarks>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[Route("api/approvals")]
public sealed class ApprovalsController : ControllerBase
{
    private readonly WebApprovalCoordinator _approvals;

    /// <summary>
    /// Initializes the HTTP approval projection.
    /// </summary>
    /// <param name="approvals">The server-owned approval coordinator.</param>
    public ApprovalsController(WebApprovalCoordinator approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        _approvals = approvals;
    }

    /// <summary>
    /// Gets approvals visible without a SignalR owner identity.
    /// </summary>
    /// <returns>An HTTP 200 response containing an empty approval list.</returns>
    [HttpGet("pending")]
    public ActionResult<IReadOnlyList<WebPendingApproval>> GetPending()
    {
        return Ok(_approvals.GetPending());
    }

    /// <summary>
    /// Attempts to decide an approval without a SignalR owner identity.
    /// </summary>
    /// <param name="requestId">The pending approval request identity.</param>
    /// <param name="decision">The requested decision; a missing body defaults to rejection.</param>
    /// <param name="cancellationToken">The token used to cancel request processing.</param>
    /// <returns>
    /// HTTP 204 if the coordinator accepts the decision, or HTTP 404 when the request is absent,
    /// completed, or owned by a SignalR connection. Under the current ownership contract, HTTP
    /// requests do not supply an owner identity and therefore cannot complete a pending decision.
    /// </returns>
    [HttpPost("{requestId}")]
    public async Task<IActionResult> Decide(string requestId, WebApprovalDecision? decision, CancellationToken cancellationToken)
    {
        var result = await _approvals.SubmitDecisionAsync(requestId, decision?.Approved ?? false, decision?.Detail, decisionConnectionId: null, cancellationToken);
        return result.Accepted
            ? NoContent()
            : NotFound(new { error = result.Message });
    }
}

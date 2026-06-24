using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[Route("api/approvals")]
public sealed class ApprovalsController : ControllerBase
{
    private readonly WebApprovalCoordinator _approvals;

    public ApprovalsController(WebApprovalCoordinator approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        _approvals = approvals;
    }

    [HttpGet("pending")]
    public ActionResult<IReadOnlyList<WebPendingApproval>> GetPending()
    {
        return Ok(_approvals.GetPending());
    }

    [HttpPost("{requestId}")]
    public async Task<IActionResult> Decide(string requestId, WebApprovalDecision? decision, CancellationToken cancellationToken)
    {
        var result = await _approvals.SubmitDecisionAsync(requestId, decision?.Approved ?? false, decision?.Detail, cancellationToken);
        return result.Accepted
            ? NoContent()
            : NotFound(new { error = result.Message });
    }
}

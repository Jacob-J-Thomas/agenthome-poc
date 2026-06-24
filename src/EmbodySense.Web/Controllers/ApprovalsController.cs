using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

[ApiController]
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
    public IActionResult Decide(string requestId, WebApprovalDecision decision)
    {
        var result = _approvals.SubmitDecision(requestId, decision.Approved, decision.Detail);
        return result.Accepted
            ? NoContent()
            : NotFound(new { error = result.Message });
    }
}

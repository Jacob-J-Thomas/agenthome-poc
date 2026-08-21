using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>Projects authenticated operational posture and exact lifecycle controls from the one retained AgentRuntime facade.</summary>
/// <remarks>The Web surface owns no queue, schedule, wake, lease, worker, run-lifecycle, authority, or backpressure policy.</remarks>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/loop-operations")]
public sealed class LoopOperationsController : ControllerBase
{
    private const int DefaultPageSize = 50;
    private readonly WebAgentRuntimeHost _host;

    /// <summary>Creates a Web projection over the single retained runtime host.</summary>
    /// <param name="host">The process-wide runtime owner.</param>
    public LoopOperationsController(WebAgentRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Reads bounded queue, schedule, wake, run, and coordinator posture without selecting work.</summary>
    /// <param name="maximumQueueEntries">The queue page bound.</param>
    /// <param name="maximumSchedules">The schedule page bound.</param>
    /// <param name="maximumWakes">The sleeping-checkpoint page bound.</param>
    /// <param name="maximumRuns">The durable-run page bound.</param>
    /// <param name="queueCursor">The opaque next-page queue cursor.</param>
    /// <param name="afterScheduleId">The exclusive schedule identity cursor.</param>
    /// <param name="afterCheckpointId">The exclusive sleeping-checkpoint identity cursor.</param>
    /// <param name="afterRunId">The exclusive durable-run identity cursor.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the posture read.</param>
    /// <returns>The authoritative posture result, or a bounded HTTP error projection.</returns>
    [HttpGet("posture")]
    public async Task<IActionResult> ReadPosture(
        [FromQuery] int maximumQueueEntries = DefaultPageSize,
        [FromQuery] int maximumSchedules = DefaultPageSize,
        [FromQuery] int maximumWakes = DefaultPageSize,
        [FromQuery] int maximumRuns = DefaultPageSize,
        [FromQuery] string? queueCursor = null,
        [FromQuery] string? afterScheduleId = null,
        [FromQuery] string? afterCheckpointId = null,
        [FromQuery] string? afterRunId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        var response = await _host.ReadGovernedLoopOperationalPostureAsync(
            maximumQueueEntries,
            maximumSchedules,
            maximumWakes,
            maximumRuns,
            queueCursor,
            afterScheduleId,
            afterCheckpointId,
            afterRunId,
            cancellationToken);
        return response.Status switch
        {
            "Available" or "Backpressured" => Ok(response.Payload),
            "Invalid" => BadRequest(response.Payload),
            "Corrupt" or "Ambiguous" => Conflict(response.Payload),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response.Payload),
        };
    }

    /// <summary>Executes one exact control advertised by the same authoritative posture snapshot.</summary>
    /// <param name="request">The idempotency identity, control token, target, optimistic evidence, and exact authority hash.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the control before its durable boundary.</param>
    /// <returns>The canonical control result with no Web-owned lifecycle policy.</returns>
    [HttpPost("control")]
    public async Task<IActionResult> Control(
        [FromBody] LoopOperationalControlRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }
        if (request is null)
        {
            return BadRequest(new { error = "operational_control_required", detail = "Exact optimistic control evidence is required." });
        }

        var response = await _host.ControlGovernedLoopOperationAsync(request, cancellationToken);
        return response.Status switch
        {
            "Applied" or "Replayed" => Ok(response.Payload),
            "Invalid" => BadRequest(response.Payload),
            "NotFound" => NotFound(response.Payload),
            "Conflict" or "Ineligible" or "Backpressured" or "OperationInProgress" or "PartiallyApplied" or "NeedsReview" or "Unauthorized" or "Corrupt" => Conflict(response.Payload),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response.Payload),
        };
    }

    private ObjectResult WorkspaceNotInitialized()
        => Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before inspecting or changing governed-loop runtime posture." });

    private bool IsWorkspaceInitialized() => _host.GetStatus().Initialized;
}

using EmbodySense.Core.Startup.Loops.Schedules.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>Projects bounded canonical schedule authoring and exact schedule rereads through the authenticated Web surface.</summary>
/// <remarks>
/// The controller owns no scheduler, state, authority, payload, revision, or edit policy. Immutable successor edits use
/// this create endpoint with a new operation identity, then the existing loop-operations control endpoint disables the
/// exact predecessor state before the successor is enabled. This visibly preserves the non-atomic transition contract.
/// </remarks>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/governed-schedules")]
public sealed class GovernedSchedulesController : ControllerBase
{
    private readonly WebAgentRuntimeHost _host;

    /// <summary>Creates the authenticated projection over the process-wide retained runtime.</summary>
    /// <param name="host">The process-wide Web runtime owner.</param>
    public GovernedSchedulesController(WebAgentRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Reads the exact time-zone choices supported by the server's retained schedule rules snapshot.</summary>
    /// <param name="cancellationToken">Cancels runtime acquisition or the server-owned catalog read.</param>
    /// <returns>The bounded server-supported identifier catalog.</returns>
    [HttpGet("time-zones")]
    public async Task<ActionResult<GovernedLoopScheduleTimeZoneCatalogResponse>> ReadTimeZones(CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            var response = await _host.ReadGovernedLoopScheduleTimeZonesAsync(cancellationToken);
            return response.Status == "available"
                ? Ok(response)
                : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
        }
        catch (Exception exception) when (IsRuntimeAcquisitionFailure(exception))
        {
            return RuntimeUnavailable();
        }
    }

    /// <summary>Rereads one exact immutable schedule definition and current optimistic state.</summary>
    /// <param name="scheduleId">The stable canonical schedule identity.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the reread.</param>
    /// <returns>The closed canonical schedule projection.</returns>
    [HttpGet("detail")]
    public async Task<ActionResult<GovernedLoopScheduleAuthoringResponse>> Read(
        [FromQuery] string scheduleId,
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            return ProjectRead(await _host.ReadGovernedLoopScheduleAsync(scheduleId, cancellationToken));
        }
        catch (Exception exception) when (IsRuntimeAcquisitionFailure(exception))
        {
            return RuntimeUnavailable();
        }
    }

    /// <summary>Creates or exactly replays one immutable canonical schedule from bounded authoring intent.</summary>
    /// <param name="input">The graph selector, exact graph lifecycle revision, bounded policies, and optional preview acknowledgement.</param>
    /// <param name="cancellationToken">Cancels before durable authority or schedule boundaries.</param>
    /// <returns>A closed result and canonical reread after every durable outcome.</returns>
    [HttpPost("create")]
    public async Task<ActionResult<GovernedLoopScheduleAuthoringResponse>> Create(
        [FromBody] GovernedLoopScheduleAuthoringInput? input,
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }
        if (input is null)
        {
            return BadRequest(new { error = "governed_schedule_authoring_required", detail = "Bounded schedule authoring intent is required." });
        }

        try
        {
            return ProjectCreate(await _host.CreateGovernedLoopScheduleAsync(input, cancellationToken));
        }
        catch (Exception exception) when (IsRuntimeAcquisitionFailure(exception))
        {
            return RuntimeUnavailable();
        }
    }

    private static ActionResult<GovernedLoopScheduleAuthoringResponse> ProjectRead(GovernedLoopScheduleAuthoringResponse response)
        => response.Status switch
        {
            "ready" => new OkObjectResult(response),
            "invalid" => new BadRequestObjectResult(response),
            "not-found" => new NotFoundObjectResult(response),
            "corrupt" => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };

    private static ActionResult<GovernedLoopScheduleAuthoringResponse> ProjectCreate(GovernedLoopScheduleAuthoringResponse response)
        => response.Status switch
        {
            "created" or "replayed" or "confirmation-required" => new OkObjectResult(response),
            "invalid" => new BadRequestObjectResult(response),
            "not-found" => new NotFoundObjectResult(response),
            "stale" or "conflict" or "ineligible" or "corrupt" => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };

    private ObjectResult WorkspaceNotInitialized()
        => Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before authoring governed schedules." });

    private ObjectResult RuntimeUnavailable()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "governed_schedule_runtime_unavailable", detail = "The retained runtime or canonical schedule host is unavailable. Retry after runtime health is restored." });

    private bool IsWorkspaceInitialized() => _host.GetStatus().Initialized;

    private static bool IsRuntimeAcquisitionFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException;
}

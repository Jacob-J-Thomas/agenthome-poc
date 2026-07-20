using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[Route("api/loop-runs")]
public sealed class LoopRunsController : ControllerBase
{
    private const int MaximumPageSize = 50;
    private readonly WebAgentRuntimeHost _host;

    public LoopRunsController(WebAgentRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LoopRunSummarySnapshot>>> List([FromQuery] int maximumCount = MaximumPageSize, CancellationToken cancellationToken = default)
    {
        if (!_host.GetStatus().Initialized)
        {
            return WorkspaceNotInitialized();
        }

        if (maximumCount is < 1 or > MaximumPageSize)
        {
            return BadRequest(new { error = "invalid_maximum_count", detail = $"maximumCount must be between 1 and {MaximumPageSize}." });
        }

        try
        {
            return Ok(await _host.GetLoopRunsAsync(maximumCount, cancellationToken));
        }
        catch (Exception exception) when (IsEvidenceReadFailure(exception))
        {
            return EvidenceUnavailable();
        }
    }

    [HttpGet("{runId}")]
    public async Task<ActionResult<LoopRunSnapshot>> Get(string runId, CancellationToken cancellationToken = default)
    {
        if (!_host.GetStatus().Initialized)
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            var run = await _host.GetLoopRunAsync(runId, cancellationToken);
            return run is null ? NotFound() : Ok(run);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_run_id", detail = "The run id is not a valid artifact identifier." });
        }
        catch (Exception exception) when (IsEvidenceReadFailure(exception))
        {
            return EvidenceUnavailable();
        }
    }

    [HttpGet("quota")]
    public async Task<ActionResult<LoopTraceQuotaSnapshot>> GetTraceQuota(CancellationToken cancellationToken = default)
    {
        if (!_host.GetStatus().Initialized)
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            return Ok(await _host.GetLoopTraceQuotaAsync(cancellationToken));
        }
        catch (Exception exception) when (IsEvidenceReadFailure(exception))
        {
            return EvidenceUnavailable();
        }
    }

    [HttpGet("{runId}/trace")]
    public async Task<ActionResult<LoopTraceInspectionSnapshot>> GetTrace(string runId, CancellationToken cancellationToken = default)
    {
        if (!_host.GetStatus().Initialized)
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            var trace = await _host.GetLoopTraceAsync(runId, cancellationToken);
            return trace is null ? NotFound() : Ok(trace);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_run_id", detail = "The run id is not a valid artifact identifier." });
        }
        catch (Exception exception) when (IsEvidenceReadFailure(exception))
        {
            return EvidenceUnavailable();
        }
    }

    [HttpPost("{runId}/trace/delete")]
    public async Task<ActionResult<LoopTraceDeletionResponse>> DeleteTrace(string runId, LoopTraceDeletionRequest? request, CancellationToken cancellationToken = default)
    {
        if (!_host.GetStatus().Initialized)
        {
            return WorkspaceNotInitialized();
        }

        if (request is null)
        {
            return BadRequest(new { error = "trace_deletion_request_required", detail = "expectedTraceHash and operationId are required." });
        }

        try
        {
            var response = await _host.DeleteLoopTraceAsync(runId, request.ExpectedTraceHash, request.OperationId, cancellationToken);
            return response.Status switch
            {
                "NotFound" => NotFound(response),
                "Invalid" => BadRequest(response),
                "Nonterminal" or "HashMismatch" or "Conflict" or "LimitExceeded" => Conflict(response),
                "AuditUnavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
                _ => Ok(response)
            };
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_trace_deletion_request", detail = "The trace deletion request is invalid." });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "trace_deletion_unavailable", detail = "The trace deletion request could not be processed safely. The retained artifact and local audit log remain authoritative." });
        }
    }

    [HttpPost("{runId}/pause")]
    public async Task<ActionResult<LoopRunControlResponse>> Pause(string runId, LoopRunLifecycleRequest? request, CancellationToken cancellationToken = default)
    {
        return await ControlAsync(runId, request, pause: true, cancellationToken);
    }

    [HttpPost("{runId}/cancel")]
    public async Task<ActionResult<LoopRunControlResponse>> Cancel(string runId, LoopRunLifecycleRequest? request, CancellationToken cancellationToken = default)
    {
        return await ControlAsync(runId, request, pause: false, cancellationToken);
    }

    private async Task<ActionResult<LoopRunControlResponse>> ControlAsync(string runId, LoopRunLifecycleRequest? request, bool pause, CancellationToken cancellationToken)
    {
        if (!_host.GetStatus().Initialized)
        {
            return WorkspaceNotInitialized();
        }

        if (request is null)
        {
            return BadRequest(new { error = "control_request_required", detail = "expectedLifecycleVersion and operationId are required." });
        }

        try
        {
            var input = new LoopRunControlInput(runId, request.ExpectedLifecycleVersion, request.OperationId);
            var response = pause
                ? await _host.PauseLoopAsync(input, cancellationToken)
                : await _host.CancelLoopAsync(input, cancellationToken);
            return response.Status switch
            {
                "NotFound" => NotFound(response),
                "Conflict" or "InvalidState" => Conflict(response),
                "Failed" => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
                _ => Ok(response)
            };
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_control_request", detail = "The custom-loop lifecycle request is invalid." });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "loop_control_unavailable", detail = "The lifecycle request could not be processed safely. Check durable run evidence and the local audit log." });
        }
    }

    private ConflictObjectResult WorkspaceNotInitialized()
    {
        return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before reading custom-loop run evidence." });
    }

    private ObjectResult EvidenceUnavailable()
    {
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "run_evidence_unavailable", detail = "Custom-loop run evidence could not be read safely. Check the local audit log for diagnostics." });
    }

    private static bool IsEvidenceReadFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException;
    }
}

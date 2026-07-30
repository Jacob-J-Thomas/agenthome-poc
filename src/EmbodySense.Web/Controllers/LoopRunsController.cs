using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace EmbodySense.Web.Controllers;

/// <summary>
/// Exposes authenticated, no-store HTTP inspection and lifecycle control for durable custom-loop runs.
/// </summary>
/// <remarks>
/// Reads trigger interrupted-run recovery before projecting evidence. Expected validation, persistence,
/// and corruption failures are translated into bounded HTTP responses without exposing local paths or
/// exception details, except for the explicit unsupported-schema cleanup message.
/// </remarks>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/loop-runs")]
public sealed class LoopRunsController : ControllerBase
{
    private const int MaximumPageSize = 50;
    private readonly WebAgentRuntimeHost _host;

    /// <summary>
    /// Initializes the run-evidence controller.
    /// </summary>
    /// <param name="host">The Web runtime host that owns recovery, inspection, and lifecycle operations.</param>
    public LoopRunsController(WebAgentRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Lists a bounded page of durable run summaries.
    /// </summary>
    /// <param name="maximumCount">The requested page size from 1 through 50.</param>
    /// <param name="loopId">An optional custom-loop artifact identifier filter.</param>
    /// <param name="cursor">An optional opaque continuation cursor returned by an earlier page.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or evidence reads.</param>
    /// <returns>
    /// HTTP 200 with a page; HTTP 400 for invalid bounds, filter, or cursor; HTTP 409 when the
    /// workspace is uninitialized; or HTTP 503 for unsupported or unreadable durable evidence.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<LoopRunSummaryPageSnapshot>> List([FromQuery] int maximumCount = MaximumPageSize, [FromQuery] string? loopId = null, [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
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
            return Ok(await _host.GetLoopRunsAsync(maximumCount, loopId, cursor, cancellationToken));
        }
        catch (LoopRunEvidenceUnsupportedSchemaException exception)
        {
            return UnsupportedPersistenceSchema(exception);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_run_query", detail = "The loop filter or continuation cursor is invalid." });
        }
        catch (Exception exception) when (IsEvidenceReadFailure(exception))
        {
            return EvidenceUnavailable();
        }
    }

    /// <summary>
    /// Gets one complete durable run snapshot.
    /// </summary>
    /// <param name="runId">The run artifact identifier.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or the evidence read.</param>
    /// <returns>
    /// HTTP 200 with the run, HTTP 400 for an invalid identifier, HTTP 404 for a missing run,
    /// HTTP 409 when the workspace is uninitialized, or HTTP 503 for unsupported or unreadable evidence.
    /// </returns>
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
        catch (LoopRunEvidenceUnsupportedSchemaException exception)
        {
            return UnsupportedPersistenceSchema(exception);
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

    /// <summary>
    /// Gets the monitor-visible summary for one run with conditional-request support.
    /// </summary>
    /// <param name="runId">The run artifact identifier.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or the monitor read.</param>
    /// <returns>
    /// HTTP 200 with the current summary and a strong entity tag; HTTP 304 when <c>If-None-Match</c>
    /// weakly matches that tag; HTTP 400 for an invalid identifier; HTTP 404 for a missing run;
    /// HTTP 409 when uninitialized; or HTTP 503 for unsupported or unreadable evidence.
    /// </returns>
    [HttpGet("{runId}/monitor")]
    public async Task<ActionResult<LoopRunSummarySnapshot>> Monitor(string runId, CancellationToken cancellationToken = default)
    {
        if (!_host.GetStatus().Initialized)
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            var monitor = await _host.GetLoopRunMonitorAsync(runId, cancellationToken);
            if (monitor is null)
            {
                return NotFound();
            }

            var etag = LoopRunMonitorEtag.Create(monitor.Summary, monitor.ArtifactHash);
            var currentEtag = EntityTagHeaderValue.Parse(etag);
            Response.GetTypedHeaders().ETag = currentEtag;
            var candidates = Request.GetTypedHeaders().IfNoneMatch;
            if (candidates is not null && candidates.Any(candidate => candidate == EntityTagHeaderValue.Any || candidate.Compare(currentEtag, useStrongComparison: false)))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return Ok(monitor.Summary);
        }
        catch (LoopRunEvidenceUnsupportedSchemaException exception)
        {
            return UnsupportedPersistenceSchema(exception);
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

    /// <summary>
    /// Gets the durable reconciliation record for an invocation operation.
    /// </summary>
    /// <param name="operationId">The caller-owned invocation operation identifier.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or the evidence read.</param>
    /// <returns>
    /// HTTP 200 with the operation, HTTP 400 for an invalid identifier, HTTP 404 when absent,
    /// HTTP 409 when uninitialized, or HTTP 503 for unsupported or unreadable evidence.
    /// </returns>
    [HttpGet("invocations/{operationId}")]
    public async Task<ActionResult<LoopInvocationOperationSnapshot>> GetInvocationOperation(string operationId, CancellationToken cancellationToken = default)
    {
        if (!_host.GetStatus().Initialized)
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            var operation = await _host.GetLoopInvocationOperationAsync(operationId, cancellationToken);
            return operation is null ? NotFound() : Ok(operation);
        }
        catch (LoopRunEvidenceUnsupportedSchemaException exception)
        {
            return UnsupportedPersistenceSchema(exception);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_invocation_operation_id", detail = "The invocation operation id is not a valid artifact identifier." });
        }
        catch (Exception exception) when (IsEvidenceReadFailure(exception))
        {
            return EvidenceUnavailable();
        }
    }

    /// <summary>
    /// Gets the durable reconciliation record for a lifecycle-control operation.
    /// </summary>
    /// <param name="operationId">The caller-owned control operation identifier.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or the evidence read.</param>
    /// <returns>
    /// HTTP 200 with the operation, HTTP 400 for an invalid identifier, HTTP 404 when absent,
    /// HTTP 409 when uninitialized, or HTTP 503 for unreadable evidence.
    /// </returns>
    [HttpGet("controls/{operationId}")]
    public async Task<ActionResult<LoopControlOperationSnapshot>> GetControlOperation(string operationId, CancellationToken cancellationToken = default)
    {
        if (!_host.GetStatus().Initialized)
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            var operation = await _host.GetLoopControlOperationAsync(operationId, cancellationToken);
            return operation is null ? NotFound() : Ok(operation);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_control_operation_id", detail = "The control operation id is not a valid artifact identifier." });
        }
        catch (Exception exception) when (IsEvidenceReadFailure(exception))
        {
            return EvidenceUnavailable();
        }
    }

    /// <summary>
    /// Gets retained-trace quota and usage for the workspace.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel recovery or the quota read.</param>
    /// <returns>
    /// HTTP 200 with quota state, HTTP 409 when uninitialized, or HTTP 503 for unsupported or
    /// unreadable evidence.
    /// </returns>
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
        catch (LoopRunEvidenceUnsupportedSchemaException exception)
        {
            return UnsupportedPersistenceSchema(exception);
        }
        catch (Exception exception) when (IsEvidenceReadFailure(exception))
        {
            return EvidenceUnavailable();
        }
    }

    /// <summary>
    /// Gets retained trace evidence for one run.
    /// </summary>
    /// <param name="runId">The run artifact identifier.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or the trace read.</param>
    /// <returns>
    /// HTTP 200 with retained trace content, HTTP 400 for an invalid identifier, HTTP 404 when
    /// no trace is retained, HTTP 409 when uninitialized, or HTTP 503 for unsupported or unreadable evidence.
    /// </returns>
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
        catch (LoopRunEvidenceUnsupportedSchemaException exception)
        {
            return UnsupportedPersistenceSchema(exception);
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

    /// <summary>
    /// Deletes retained trace content using its expected hash and an idempotent operation identity.
    /// </summary>
    /// <param name="runId">The owning run artifact identifier.</param>
    /// <param name="request">The expected trace hash and caller-owned deletion operation identity.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or deletion.</param>
    /// <returns>
    /// HTTP 200 for a completed or replayed deletion; HTTP 400 for a missing or invalid request;
    /// HTTP 404 when the run or trace is absent; HTTP 409 for lifecycle, hash, capacity, or operation
    /// conflicts; HTTP 409 when uninitialized; or HTTP 503 when schema, audit, or persistence safety
    /// prevents deletion.
    /// </returns>
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
                "Nonterminal" or "HashMismatch" or "Conflict" or "LimitExceeded" or "OperationLimitExceeded" or "OperationInProgress" => Conflict(response),
                "AuditUnavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
                _ => Ok(response)
            };
        }
        catch (LoopRunEvidenceUnsupportedSchemaException exception)
        {
            return UnsupportedPersistenceSchema(exception);
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

    /// <summary>
    /// Requests an idempotent transition from a running custom loop to its paused state.
    /// </summary>
    /// <param name="runId">The run artifact identifier.</param>
    /// <param name="request">The expected lifecycle version and caller-owned operation identity.</param>
    /// <param name="cancellationToken">The token used to cancel control processing.</param>
    /// <returns>
    /// HTTP 200 for a completed or replayed operation; HTTP 400 for a missing or invalid request;
    /// HTTP 404 when the run is absent; HTTP 409 for state, version, host, operation, or initialization
    /// conflicts; or HTTP 503 when schema, persistence, or runtime safety prevents control.
    /// </returns>
    [HttpPost("{runId}/pause")]
    public async Task<ActionResult<LoopRunControlResponse>> Pause(string runId, LoopRunLifecycleRequest? request, CancellationToken cancellationToken = default)
    {
        return await ControlAsync(runId, request, pause: true, cancellationToken);
    }

    /// <summary>
    /// Requests an idempotent transition from a nonterminal custom loop to its cancelled state.
    /// </summary>
    /// <param name="runId">The run artifact identifier.</param>
    /// <param name="request">The expected lifecycle version and caller-owned operation identity.</param>
    /// <param name="cancellationToken">The token used to cancel control processing.</param>
    /// <returns>
    /// HTTP 200 for a completed or replayed operation; HTTP 400 for a missing or invalid request;
    /// HTTP 404 when the run is absent; HTTP 409 for state, version, host, operation, or initialization
    /// conflicts; or HTTP 503 when schema, persistence, or runtime safety prevents control.
    /// </returns>
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
                "Conflict" or "InvalidState" or "WorkspaceExecutionBusy" or "OperationInProgress" => Conflict(response),
                "Failed" or "WorkspaceHostUnavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
                _ => Ok(response)
            };
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_control_request", detail = "The custom-loop lifecycle request is invalid." });
        }
        catch (LoopRunEvidenceUnsupportedSchemaException exception)
        {
            return UnsupportedPersistenceSchema(exception);
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

    private ObjectResult UnsupportedPersistenceSchema(LoopRunEvidenceUnsupportedSchemaException exception)
    {
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "unsupported_loop_persistence_schema", detail = exception.Message });
    }

    private static bool IsEvidenceReadFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException;
    }
}

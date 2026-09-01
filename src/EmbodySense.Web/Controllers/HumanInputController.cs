using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>Exposes authenticated no-store Web controls over the canonical Human Input posture and lifecycle contract.</summary>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/human-input")]
public sealed class HumanInputController : ControllerBase
{
    private const int MaximumPageSize = 50;
    private readonly IWebHumanInputRuntime _runtime;

    /// <summary>Creates the Web Human Input controller over one Startup-backed runtime boundary.</summary>
    /// <param name="runtime">The canonical retained Human Input runtime boundary.</param>
    public HumanInputController(IWebHumanInputRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>Lists one bounded page of redacted canonical Human Input posture.</summary>
    [HttpGet]
    public async Task<ActionResult<HumanInputRequestPosturePage>> List([FromQuery] int maximumCount = MaximumPageSize, [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > MaximumPageSize)
        {
            return BadRequest(new { error = "invalid_page" });
        }

        return Project(await _runtime.ListAsync(new HumanInputRequestPosturePageRequest(maximumCount, cursor), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads one exact redacted canonical Human Input request posture.</summary>
    [HttpGet("{requestId}")]
    public async Task<ActionResult<HumanInputRequestPosture>> Get(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return BadRequest(new { error = "invalid_request_id" });
        }

        var result = await _runtime.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            HumanInputRequestPostureReadStatus.Ready when result.Request is not null => Ok(result.Request),
            HumanInputRequestPostureReadStatus.Invalid => BadRequest(new { error = "invalid_request_id" }),
            HumanInputRequestPostureReadStatus.NotFound => NotFound(new { error = "human_input_not_found" }),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "human_input_unavailable" })
        };
    }

    /// <summary>Answers one exact pending Human Input request with bounded untrusted response data.</summary>
    [HttpPost("{requestId}/answer")]
    public Task<ActionResult<HumanInputOperationResult>> Answer(string requestId, [FromBody] HumanInputWebResponseRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null || !MatchesRoute(requestId, request.ExpectedRequest))
        {
            return Task.FromResult<ActionResult<HumanInputOperationResult>>(Conflict(new { error = "request_state_conflict" }));
        }

        return SubmitResponseAsync(requestId, request, cancellationToken);
    }

    /// <summary>Rejects one exact pending Human Input request through server-owned lifecycle authority.</summary>
    [HttpPost("{requestId}/reject")]
    public Task<ActionResult<HumanInputOperationResult>> Reject(string requestId, [FromBody] HumanInputWebLifecycleRequest? request, CancellationToken cancellationToken = default)
        => SubmitLifecycleAsync(requestId, request, "Reject", cancellationToken);

    /// <summary>Cancels one exact pending Human Input request through server-owned lifecycle authority.</summary>
    [HttpPost("{requestId}/cancel")]
    public Task<ActionResult<HumanInputOperationResult>> Cancel(string requestId, [FromBody] HumanInputWebLifecycleRequest? request, CancellationToken cancellationToken = default)
        => SubmitLifecycleAsync(requestId, request, "Cancel", cancellationToken);

    /// <summary>Prepares one opaque server-owned successor candidate for supersession.</summary>
    [HttpPost("{requestId}/supersede/prepare")]
    public async Task<ActionResult<HumanInputSupersedePreparationResult>> PrepareSupersede(string requestId, [FromBody] HumanInputWebSupersedePreparationRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null || !MatchesRoute(requestId, request.ExpectedRequest))
        {
            return Conflict(new { error = "request_state_conflict" });
        }

        if (request.Successor is null)
        {
            return BadRequest(new { error = "successor_required" });
        }

        var input = new HumanInputSupersedePreparationInput(
            request.OperationId,
            requestId,
            ToSurfaceReference(request.ExpectedRequest)!,
            request.ExpectedLifecycleVersion,
            request.ExpectedLifecycleStatus,
            request.Successor.Purpose,
            request.Successor.Prompt,
            request.Successor.ResponseSchema,
            request.Successor.PrivacyClass,
            request.Successor.ExpiresAtUtc,
            request.Successor.ResponsePolicy);
        return Project(await _runtime.PrepareSupersedeAsync(input, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Commits one exact prepared successor candidate through server-owned lifecycle authority.</summary>
    [HttpPost("{requestId}/supersede")]
    public Task<ActionResult<HumanInputOperationResult>> Supersede(string requestId, [FromBody] HumanInputWebLifecycleRequest? request, CancellationToken cancellationToken = default)
        => SubmitLifecycleAsync(requestId, request, "Supersede", cancellationToken);

    private async Task<ActionResult<HumanInputOperationResult>> SubmitLifecycleAsync(string requestId, HumanInputWebLifecycleRequest? request, string kind, CancellationToken cancellationToken)
    {
        if (request is null || !MatchesRoute(requestId, request.ExpectedRequest))
        {
            return Conflict(new { error = "request_state_conflict" });
        }

        if (string.Equals(kind, "Supersede", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(request.CandidateKey))
        {
            return BadRequest(new { error = "candidate_key_required" });
        }

        var result = await _runtime.SubmitLifecycleAsync(
            new HumanInputSurfaceLifecycleOperationInput(
                request.OperationId,
                kind,
                requestId,
                request.ExpectedLifecycleVersion,
                request.ExpectedLifecycleStatus,
                ToSurfaceReference(request.ExpectedRequest),
                request.CandidateKey,
                request.Reason),
            cancellationToken).ConfigureAwait(false);
        return Project(result);
    }

    private async Task<ActionResult<HumanInputOperationResult>> SubmitResponseAsync(string requestId, HumanInputWebResponseRequest request, CancellationToken cancellationToken)
    {
        var result = await _runtime.SubmitResponseAsync(
            new HumanInputSurfaceResponseOperationInput(
                request.OperationId,
                "Submit",
                requestId,
                request.ExpectedLifecycleVersion,
                request.ExpectedLifecycleStatus,
                ToSurfaceReference(request.ExpectedRequest)!,
                request.ResponseId ?? string.Empty,
                request.Value,
                request.Explanation),
            cancellationToken).ConfigureAwait(false);
        return Project(result);
    }

    private static bool MatchesRoute(string requestId, HumanInputWebRequestReference? expectedRequest)
        => expectedRequest is not null && string.Equals(requestId, expectedRequest.RequestId, StringComparison.Ordinal);

    private static HumanInputSurfaceRequestReference? ToSurfaceReference(HumanInputWebRequestReference? reference)
        => reference is null ? null : new HumanInputSurfaceRequestReference(reference.RequestId, reference.RequestVersionId, reference.RequestHash);

    private static ActionResult<HumanInputRequestPosturePage> Project(HumanInputRequestPosturePage response)
        => response.Status switch
        {
            HumanInputRequestPosturePageStatus.Ready => new OkObjectResult(response),
            HumanInputRequestPosturePageStatus.Invalid => new BadRequestObjectResult(response),
            HumanInputRequestPosturePageStatus.Stale => new ConflictObjectResult(response),
            _ => new ObjectResult(new { error = "human_input_unavailable" }) { StatusCode = StatusCodes.Status503ServiceUnavailable }
        };

    private static ActionResult<HumanInputSupersedePreparationResult> Project(HumanInputSupersedePreparationResult response)
        => response.Status switch
        {
            HumanInputSupersedePreparationStatus.Ready => new OkObjectResult(response),
            HumanInputSupersedePreparationStatus.Invalid => new BadRequestObjectResult(response),
            HumanInputSupersedePreparationStatus.NotFound => new NotFoundObjectResult(response),
            HumanInputSupersedePreparationStatus.Conflict or HumanInputSupersedePreparationStatus.Ambiguous => new ConflictObjectResult(response),
            HumanInputSupersedePreparationStatus.Denied => new ObjectResult(response) { StatusCode = StatusCodes.Status403Forbidden },
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable }
        };

    private static ActionResult<HumanInputOperationResult> Project(HumanInputOperationResult response)
        => response.Status switch
        {
            HumanInputOperationStatus.Committed or HumanInputOperationStatus.Replayed => new OkObjectResult(response),
            HumanInputOperationStatus.Invalid => new BadRequestObjectResult(response),
            HumanInputOperationStatus.NotFound => new NotFoundObjectResult(response),
            HumanInputOperationStatus.Denied => new ObjectResult(response) { StatusCode = StatusCodes.Status403Forbidden },
            HumanInputOperationStatus.Conflict or HumanInputOperationStatus.Late or HumanInputOperationStatus.Ambiguous => new ConflictObjectResult(response),
            _ => new ObjectResult(new { error = "human_input_unavailable" }) { StatusCode = StatusCodes.Status503ServiceUnavailable }
        };
}

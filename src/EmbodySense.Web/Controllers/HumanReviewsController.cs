using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>Projects the canonical durable Human Review facade through the authenticated Web surface.</summary>
/// <remarks>
/// This controller owns only HTTP validation and status projection. The retained runtime owns persistence,
/// authority, optimistic concurrency, decision semantics, effect revalidation, and recovery. Request bodies never
/// supply an actor, role, scope, grant, or connection identity; each route supplies its own closed decision kind.
/// </remarks>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(MaximumRequestBodyBytes)]
[Route("api/human-reviews")]
public sealed class HumanReviewsController : ControllerBase
{
    private const int MaximumPageSize = 50;
    private const long MaximumRequestBodyBytes = 16_384;
    private readonly IWebHumanReviewRuntime _runtime;
    private readonly IWebHumanReviewNotifier _notifier;
    private readonly ILogger<HumanReviewsController> _logger;

    /// <summary>Creates the authenticated projection over the retained Human Review runtime.</summary>
    /// <param name="runtime">The process-wide runtime facade that owns canonical Human Review operations.</param>
    /// <param name="notifier">The value-free refresh notifier used after a new durable decision.</param>
    /// <param name="logger">The logger used when notification delivery fails after a durable decision.</param>
    public HumanReviewsController(IWebHumanReviewRuntime runtime, IWebHumanReviewNotifier notifier, ILogger<HumanReviewsController> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Lists one bounded page of detached Human Review projections.</summary>
    /// <param name="maximumCount">The requested page size from 1 through 50.</param>
    /// <param name="cursor">The opaque continuation cursor returned by the previous page.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the durable read.</param>
    /// <returns>The bounded page or its safe HTTP failure projection.</returns>
    [HttpGet]
    public async Task<ActionResult<HumanReviewPage>> List([FromQuery] int maximumCount = MaximumPageSize, [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        if (maximumCount is < 1 or > MaximumPageSize)
        {
            return BadRequest(new { error = "invalid_maximum_count", detail = $"maximumCount must be between 1 and {MaximumPageSize}." });
        }

        try
        {
            var response = await _runtime.ListHumanReviewsAsync(new HumanReviewPageRequest(maximumCount, cursor), cancellationToken).ConfigureAwait(false);
            return response is null ? RuntimeUnavailable() : ProjectList(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RuntimeUnavailable(exception);
        }
    }

    /// <summary>Reads one exact detached Human Review detail projection.</summary>
    /// <param name="runId">The exact durable run identity.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the durable read.</param>
    /// <returns>The detached detail or a safe HTTP failure projection.</returns>
    [HttpGet("{runId}")]
    public async Task<ActionResult<HumanReviewReadResult>> Read(string runId, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            var response = await _runtime.ReadHumanReviewAsync(runId, cancellationToken).ConfigureAwait(false);
            return response is null ? RuntimeUnavailable() : ProjectRead(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RuntimeUnavailable(exception);
        }
    }

    /// <summary>Reads one exact detached Human Review evidence projection.</summary>
    /// <param name="runId">The exact durable run identity.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the durable read.</param>
    /// <returns>The append-only detached evidence and value-free effect posture.</returns>
    [HttpGet("{runId}/evidence")]
    public async Task<ActionResult<HumanReviewEvidenceReadResult>> ReadEvidence(string runId, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            var response = await _runtime.ReadHumanReviewEvidenceAsync(runId, cancellationToken).ConfigureAwait(false);
            return response is null ? RuntimeUnavailable() : ProjectEvidence(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RuntimeUnavailable(exception);
        }
    }

    /// <summary>Reads one exact detached Human Review runtime posture.</summary>
    /// <param name="runId">The exact durable run identity.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the posture read.</param>
    /// <returns>The value-free runtime posture or a safe HTTP failure projection.</returns>
    [HttpGet("{runId}/posture")]
    public async Task<ActionResult<HumanReviewRuntimePostureReadResult>> ReadPosture(string runId, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        try
        {
            var response = await _runtime.ReadHumanReviewPostureAsync(runId, cancellationToken).ConfigureAwait(false);
            return response is null ? RuntimeUnavailable() : ProjectPosture(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RuntimeUnavailable(exception);
        }
    }

    /// <summary>Records consent for one exact review after server-owned authority validation.</summary>
    /// <remarks>The canonical release path separately revalidates current authority and effect evidence before dispatch.</remarks>
    /// <param name="runId">The exact durable run identity supplied by the route.</param>
    /// <param name="request">The bounded optimistic version, operation identity, and optional detail.</param>
    /// <param name="cancellationToken">Cancels before or during the durable decision boundary.</param>
    /// <returns>The detached durable decision result.</returns>
    [HttpPost("{runId}/approve")]
    public Task<ActionResult<HumanReviewDecisionResult>> Approve(string runId, [FromBody] WebHumanReviewDecisionRequest? request, CancellationToken cancellationToken = default)
        => Decide(runId, HumanReviewDecisionKind.Approve, request, cancellationToken);

    /// <summary>Rejects one exact review through its authored failure route.</summary>
    /// <param name="runId">The exact durable run identity supplied by the route.</param>
    /// <param name="request">The bounded optimistic version and operation identity.</param>
    /// <param name="cancellationToken">Cancels before or during the durable decision boundary.</param>
    /// <returns>The detached durable decision result.</returns>
    [HttpPost("{runId}/reject")]
    public Task<ActionResult<HumanReviewDecisionResult>> Reject(string runId, [FromBody] WebHumanReviewDecisionRequest? request, CancellationToken cancellationToken = default)
        => Decide(runId, HumanReviewDecisionKind.Reject, request, cancellationToken);

    /// <summary>Cancels one exact review through its authored lifecycle boundary.</summary>
    /// <param name="runId">The exact durable run identity supplied by the route.</param>
    /// <param name="request">The bounded optimistic version and operation identity.</param>
    /// <param name="cancellationToken">Cancels before or during the durable decision boundary.</param>
    /// <returns>The detached durable decision result.</returns>
    [HttpPost("{runId}/cancel")]
    public Task<ActionResult<HumanReviewDecisionResult>> Cancel(string runId, [FromBody] WebHumanReviewDecisionRequest? request, CancellationToken cancellationToken = default)
        => Decide(runId, HumanReviewDecisionKind.Cancel, request, cancellationToken);

    /// <summary>Requests bounded additional information while retaining the parked review.</summary>
    /// <param name="runId">The exact durable run identity supplied by the route.</param>
    /// <param name="request">The bounded optimistic version, operation identity, and required detail.</param>
    /// <param name="cancellationToken">Cancels before or during the durable decision boundary.</param>
    /// <returns>The detached durable decision result.</returns>
    [HttpPost("{runId}/request-information")]
    public Task<ActionResult<HumanReviewDecisionResult>> RequestInformation(string runId, [FromBody] WebHumanReviewDecisionRequest? request, CancellationToken cancellationToken = default)
        => Decide(runId, HumanReviewDecisionKind.RequestInformation, request, cancellationToken);

    private async Task<ActionResult<HumanReviewDecisionResult>> Decide(string runId, HumanReviewDecisionKind kind, WebHumanReviewDecisionRequest? request, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        if (request is null)
        {
            return BadRequest(new { error = "human_review_decision_required", detail = "The bounded Human Review decision body is required." });
        }

        if (kind is HumanReviewDecisionKind.Approve or HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel)
        {
            if (request.Detail is not null)
            {
                return BadRequest(new { error = "human_review_detail_not_allowed", detail = "Decision detail is accepted only when requesting information." });
            }
        }
        else if (string.IsNullOrWhiteSpace(request.Detail))
        {
            return BadRequest(new { error = "human_review_detail_required", detail = "A bounded detail is required when requesting information." });
        }

        try
        {
            var response = await _runtime.DecideHumanReviewAsync(new HumanReviewDecisionOperationInput(runId, request.ExpectedLifecycleVersion, request.OperationId ?? string.Empty, kind, request.Detail), cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return RuntimeUnavailable();
            }

            if (response.Status is HumanReviewDecisionStatus.Accepted or HumanReviewDecisionStatus.InformationRequested)
            {
                await NotifyAsync(runId, cancellationToken).ConfigureAwait(false);
            }

            return ProjectDecision(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RuntimeUnavailable(exception);
        }
    }

    private static ActionResult<HumanReviewPage> ProjectList(HumanReviewPage response)
        => response.Status switch
        {
            HumanReviewPageStatus.Ready => new OkObjectResult(response),
            HumanReviewPageStatus.Invalid => new BadRequestObjectResult(response),
            HumanReviewPageStatus.Ambiguous or HumanReviewPageStatus.Corrupt => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };

    private static ActionResult<HumanReviewReadResult> ProjectRead(HumanReviewReadResult response)
    {
        if (response.Status == HumanReviewReadStatus.Ready && response.Detail?.EffectEvidence is { Status: HumanReviewEffectEvidenceStatus.Invalid or HumanReviewEffectEvidenceStatus.Ambiguous or HumanReviewEffectEvidenceStatus.Corrupt or HumanReviewEffectEvidenceStatus.Stale })
        {
            return new ConflictObjectResult(response);
        }

        if (response.Status == HumanReviewReadStatus.Ready && response.Detail?.EffectEvidence?.Status == HumanReviewEffectEvidenceStatus.Unavailable)
        {
            return new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }

        return response.Status switch
        {
            HumanReviewReadStatus.Ready => new OkObjectResult(response),
            HumanReviewReadStatus.Invalid => new BadRequestObjectResult(response),
            HumanReviewReadStatus.NotFound => new NotFoundObjectResult(response),
            HumanReviewReadStatus.Corrupt => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };
    }

    private static ActionResult<HumanReviewEvidenceReadResult> ProjectEvidence(HumanReviewEvidenceReadResult response)
    {
        if (response.Status == HumanReviewEvidenceReadStatus.Ready && response.EffectEvidence is { Status: HumanReviewEffectEvidenceStatus.Invalid or HumanReviewEffectEvidenceStatus.Ambiguous or HumanReviewEffectEvidenceStatus.Corrupt or HumanReviewEffectEvidenceStatus.Stale })
        {
            return new ConflictObjectResult(response);
        }

        if (response.Status == HumanReviewEvidenceReadStatus.Ready && response.EffectEvidence?.Status == HumanReviewEffectEvidenceStatus.Unavailable)
        {
            return new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }

        return response.Status switch
        {
            HumanReviewEvidenceReadStatus.Ready => new OkObjectResult(response),
            HumanReviewEvidenceReadStatus.Invalid => new BadRequestObjectResult(response),
            HumanReviewEvidenceReadStatus.NotFound => new NotFoundObjectResult(response),
            HumanReviewEvidenceReadStatus.Corrupt => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };
    }

    private static ActionResult<HumanReviewRuntimePostureReadResult> ProjectPosture(HumanReviewRuntimePostureReadResult response)
        => response.Status switch
        {
            HumanReviewReadStatus.Ready => new OkObjectResult(response),
            HumanReviewReadStatus.Invalid => new BadRequestObjectResult(response),
            HumanReviewReadStatus.NotFound => new NotFoundObjectResult(response),
            HumanReviewReadStatus.Corrupt => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };

    private static ActionResult<HumanReviewDecisionResult> ProjectDecision(HumanReviewDecisionResult response)
        => response.Status switch
        {
            HumanReviewDecisionStatus.Accepted or HumanReviewDecisionStatus.InformationRequested or HumanReviewDecisionStatus.Replayed => new OkObjectResult(response),
            HumanReviewDecisionStatus.Invalid => new BadRequestObjectResult(response),
            HumanReviewDecisionStatus.NotFound => new NotFoundObjectResult(response),
            HumanReviewDecisionStatus.Denied => new ObjectResult(response) { StatusCode = StatusCodes.Status403Forbidden },
            HumanReviewDecisionStatus.Conflict or HumanReviewDecisionStatus.Expired or HumanReviewDecisionStatus.LimitExceeded => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };

    private async Task NotifyAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.HumanReviewChangedAsync(new WebHumanReviewChanged(runId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Human Review durable decision completed but refresh notification failed with {ExceptionType}.", exception.GetType().Name);
        }
    }

    private bool IsWorkspaceInitialized() => _runtime.IsWorkspaceInitialized;

    private ObjectResult WorkspaceNotInitialized()
        => Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before inspecting or deciding Human Review." });

    private ObjectResult RuntimeUnavailable(Exception? exception = null)
    {
        if (exception is not null)
        {
            _logger.LogWarning("The retained Human Review runtime is unavailable after {ExceptionType}.", exception.GetType().Name);
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "human_review_runtime_unavailable", detail = "The retained Human Review runtime is unavailable. Retry after runtime health is restored." });
    }
}

using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>Projects the canonical effect-reconciliation facade through authenticated bounded Web routes.</summary>
/// <remarks>
/// This controller owns only primitive HTTP validation and safe status projection. Startup owns canonical case state,
/// optimistic concurrency, authority, evidence, idempotency, and disposition semantics. Browser requests cannot open
/// cases, publish resolutions, supply actor or scope identity, upload evidence, or assert an effect outcome.
/// </remarks>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(MaximumRequestBodyBytes)]
[Route("api/effect-reconciliation")]
public sealed class EffectReconciliationController : ControllerBase
{
    private const int MaximumPageSize = 50;
    private const long MaximumRequestBodyBytes = 16_384;
    private readonly IWebEffectReconciliationRuntime _runtime;

    /// <summary>Creates the authenticated projection over the retained reconciliation runtime.</summary>
    /// <param name="runtime">The single process-wide runtime facade that owns canonical reconciliation state.</param>
    public EffectReconciliationController(IWebEffectReconciliationRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>Lists one bounded page of detached reconciliation attention cases.</summary>
    /// <param name="maximumCount">The requested page size from 1 through 50.</param>
    /// <param name="cursor">The opaque continuation cursor from the prior page.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical read.</param>
    /// <returns>The canonical page or its safe HTTP failure projection.</returns>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int maximumCount = MaximumPageSize, [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
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
            var response = await _runtime.ListAsync(new GovernedLoopEffectReconciliationPageRequest(maximumCount, cursor), cancellationToken).ConfigureAwait(false);
            return ProjectPage(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_reconciliation_page", detail = "The reconciliation page request is malformed or outside its finite bounds." });
        }
        catch
        {
            return RuntimeUnavailable();
        }
    }

    /// <summary>Lists one bounded page of registered read-only probe contracts.</summary>
    /// <param name="maximumCount">The requested page size from 1 through 50.</param>
    /// <param name="cursor">The opaque continuation cursor from the prior probe page.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical read.</param>
    /// <returns>The canonical probe catalog or its safe HTTP failure projection.</returns>
    [HttpGet("probes")]
    public async Task<IActionResult> ListProbes([FromQuery] int maximumCount = MaximumPageSize, [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
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
            var response = await _runtime.ListProbeContractsAsync(new GovernedLoopEffectReconciliationPageRequest(maximumCount, cursor), cancellationToken).ConfigureAwait(false);
            return ProjectProbePage(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_reconciliation_page", detail = "The reconciliation probe page request is malformed or outside its finite bounds." });
        }
        catch
        {
            return RuntimeUnavailable();
        }
    }

    /// <summary>Reads one exact detached immutable reconciliation case.</summary>
    /// <param name="caseId">The route-derived case identity.</param>
    /// <param name="caseVersion">The exact optimistic case version.</param>
    /// <param name="contentHash">The exact immutable case content hash.</param>
    /// <param name="bindingHash">The redacted exact execution-binding hash.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical read.</param>
    /// <returns>The canonical detached case or its safe HTTP failure projection.</returns>
    [HttpGet("{caseId}")]
    public async Task<IActionResult> Read(string caseId, [FromQuery] long caseVersion, [FromQuery] string? contentHash, [FromQuery] string? bindingHash, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        if (!TryCreateReference(caseId, new WebEffectReconciliationCaseReference(caseId, caseVersion, contentHash, bindingHash), out var reference))
        {
            return BadRequest(new { error = "invalid_case_reference", detail = "An exact case version and hash binding is required." });
        }

        try
        {
            var response = await _runtime.ReadAsync(reference!, cancellationToken).ConfigureAwait(false);
            return ProjectRead(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return RuntimeUnavailable();
        }
    }

    /// <summary>Reads one exact immutable reconciliation resolution as a postcondition.</summary>
    /// <param name="caseId">The route-derived case identity.</param>
    /// <param name="caseVersion">The exact optimistic case version.</param>
    /// <param name="contentHash">The exact immutable case content hash.</param>
    /// <param name="bindingHash">The redacted exact execution-binding hash.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical read.</param>
    /// <returns>The detached immutable resolution or its safe HTTP failure projection.</returns>
    [HttpGet("{caseId}/resolution")]
    public async Task<IActionResult> ReadResolution(string caseId, [FromQuery] long caseVersion, [FromQuery] string? contentHash, [FromQuery] string? bindingHash, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        if (!TryCreateReference(caseId, new WebEffectReconciliationCaseReference(caseId, caseVersion, contentHash, bindingHash), out var reference))
        {
            return BadRequest(new { error = "invalid_case_reference", detail = "An exact case version and hash binding is required." });
        }

        try
        {
            var response = await _runtime.ReadResolutionAsync(reference!, cancellationToken).ConfigureAwait(false);
            return ProjectResolution(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return RuntimeUnavailable();
        }
    }

    /// <summary>Invokes one registered read-only probe for an exact case.</summary>
    /// <param name="caseId">The route-derived case identity.</param>
    /// <param name="request">The exact case reference and client idempotency identity.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical operation.</param>
    /// <returns>The canonical operation result or its safe HTTP failure projection.</returns>
    [HttpPost("{caseId}/probe")]
    public Task<IActionResult> Probe(string caseId, [FromBody] WebEffectReconciliationOperationRequest? request, CancellationToken cancellationToken = default)
        => Operate(caseId, request, (operationId, reference, _, token) => _runtime.ProbeAsync(operationId, reference, token), cancellationToken, rejectSafeDetail: true);

    /// <summary>Derives one immutable assessment from current canonical observations.</summary>
    /// <param name="caseId">The route-derived case identity.</param>
    /// <param name="request">The exact case reference, operation identity, and bounded operator detail.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical operation.</param>
    /// <returns>The canonical operation result or its safe HTTP failure projection.</returns>
    [HttpPost("{caseId}/assess")]
    public Task<IActionResult> Assess(string caseId, [FromBody] WebEffectReconciliationOperationRequest? request, CancellationToken cancellationToken = default)
        => Operate(caseId, request, (operationId, reference, safeDetail, token) => _runtime.AssessAsync(operationId, reference, safeDetail, token), cancellationToken);

    /// <summary>Applies one legal disposition to the exact current assessment.</summary>
    /// <param name="caseId">The route-derived case identity.</param>
    /// <param name="request">The exact case reference, operation identity, disposition, and bounded detail.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical operation.</param>
    /// <returns>The canonical operation result or its safe HTTP failure projection.</returns>
    [HttpPost("{caseId}/dispose")]
    public Task<IActionResult> Dispose(string caseId, [FromBody] WebEffectReconciliationDispositionRequest? request, CancellationToken cancellationToken = default)
        => Operate(caseId, request?.Case, request?.OperationId, request?.SafeDetail, request?.DispositionKind, (operationId, reference, safeDetail, kind, token) => _runtime.ApplyDispositionAsync(operationId, reference, kind, safeDetail, token), cancellationToken);

    private async Task<IActionResult> Operate(string caseId, WebEffectReconciliationOperationRequest? request, Func<string, GovernedLoopEffectReconciliationCaseReference, string?, CancellationToken, Task<GovernedLoopEffectReconciliationOperationResult>> operation, CancellationToken cancellationToken, bool rejectSafeDetail = false)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        if (request?.Case is null || !TryCreateReference(caseId, request.Case, out var reference) || string.IsNullOrWhiteSpace(request.OperationId)
            || rejectSafeDetail && request.SafeDetail is not null)
        {
            return BadRequest(new { error = "invalid_operation_request", detail = rejectSafeDetail ? "A probe accepts no operator detail." : "An exact case reference and operation identity are required." });
        }

        try
        {
            var response = await operation(request.OperationId!, reference!, request.SafeDetail, cancellationToken).ConfigureAwait(false);
            return await ProjectOperationAsync(response, reference!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return RuntimeUnavailable();
        }
    }

    private async Task<IActionResult> Operate(string caseId, WebEffectReconciliationCaseReference? requestCase, string? operationId, string? safeDetail, GovernedLoopEffectReconciliationDispositionKind? kind, Func<string, GovernedLoopEffectReconciliationCaseReference, string?, GovernedLoopEffectReconciliationDispositionKind, CancellationToken, Task<GovernedLoopEffectReconciliationOperationResult>> operation, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        if (requestCase is null || !TryCreateReference(caseId, requestCase, out var reference) || string.IsNullOrWhiteSpace(operationId) || kind is null || kind == GovernedLoopEffectReconciliationDispositionKind.Unknown || !Enum.IsDefined(kind.Value))
        {
            return BadRequest(new { error = "invalid_disposition_request", detail = "An exact case reference, operation identity, and legal disposition are required." });
        }

        try
        {
            var response = await operation(operationId!, reference!, safeDetail, kind.Value, cancellationToken).ConfigureAwait(false);
            return await ProjectOperationAsync(response, reference!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return RuntimeUnavailable();
        }
    }

    private async Task<IActionResult> ProjectOperationAsync(GovernedLoopEffectReconciliationOperationResult response, GovernedLoopEffectReconciliationCaseReference reference, CancellationToken cancellationToken)
    {
        if (response is null)
        {
            return RuntimeUnavailable();
        }

        if (response.Status is GovernedLoopEffectReconciliationOperationStatus.Applied
            or GovernedLoopEffectReconciliationOperationStatus.Replayed
            or GovernedLoopEffectReconciliationOperationStatus.Found
            or GovernedLoopEffectReconciliationOperationStatus.Conflict)
        {
            var reread = await _runtime.ReadAsync(response.Detail?.Reference ?? reference, cancellationToken).ConfigureAwait(false);
            if (reread.Status == GovernedLoopEffectReconciliationReadStatus.Found && reread.Detail is not null)
            {
                response = new GovernedLoopEffectReconciliationOperationResult(response.Status, reread.Detail);
            }
            else if (reread.Status == GovernedLoopEffectReconciliationReadStatus.Unavailable)
            {
                return RuntimeUnavailable();
            }
            else
            {
                response = new GovernedLoopEffectReconciliationOperationResult(response.Status, null);
            }
        }

        return response.Status switch
        {
            GovernedLoopEffectReconciliationOperationStatus.Applied or GovernedLoopEffectReconciliationOperationStatus.Replayed or GovernedLoopEffectReconciliationOperationStatus.Found => Ok(response),
            GovernedLoopEffectReconciliationOperationStatus.Invalid => BadRequest(response),
            GovernedLoopEffectReconciliationOperationStatus.NotFound => NotFound(response),
            GovernedLoopEffectReconciliationOperationStatus.Denied => StatusCode(StatusCodes.Status403Forbidden, response),
            GovernedLoopEffectReconciliationOperationStatus.Conflict or GovernedLoopEffectReconciliationOperationStatus.Corrupt or GovernedLoopEffectReconciliationOperationStatus.CapacityExceeded or GovernedLoopEffectReconciliationOperationStatus.RepairRequired => Conflict(response),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
        };
    }

    private static IActionResult ProjectPage(GovernedLoopEffectReconciliationPage response)
        => response.Status switch
        {
            GovernedLoopEffectReconciliationPageStatus.Ready => new OkObjectResult(response),
            GovernedLoopEffectReconciliationPageStatus.Invalid => new BadRequestObjectResult(response),
            GovernedLoopEffectReconciliationPageStatus.Corrupt => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };

    private static IActionResult ProjectProbePage(GovernedLoopEffectReconciliationProbeCatalogPage response)
        => response.Status switch
        {
            GovernedLoopEffectReconciliationProbeCatalogStatus.Ready => new OkObjectResult(response),
            GovernedLoopEffectReconciliationProbeCatalogStatus.Invalid => new BadRequestObjectResult(response),
            GovernedLoopEffectReconciliationProbeCatalogStatus.Corrupt => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };

    private static IActionResult ProjectRead(GovernedLoopEffectReconciliationReadResult response)
        => response.Status switch
        {
            GovernedLoopEffectReconciliationReadStatus.Found => new OkObjectResult(response),
            GovernedLoopEffectReconciliationReadStatus.Invalid => new BadRequestObjectResult(response),
            GovernedLoopEffectReconciliationReadStatus.NotFound => new NotFoundObjectResult(response),
            GovernedLoopEffectReconciliationReadStatus.Corrupt => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };

    private static IActionResult ProjectResolution(GovernedLoopEffectReconciliationResolutionReadResult response)
        => response.Status switch
        {
            GovernedLoopEffectReconciliationResolutionReadStatus.Found => new OkObjectResult(response),
            GovernedLoopEffectReconciliationResolutionReadStatus.Invalid => new BadRequestObjectResult(response),
            GovernedLoopEffectReconciliationResolutionReadStatus.NotFound => new NotFoundObjectResult(response),
            GovernedLoopEffectReconciliationResolutionReadStatus.Corrupt => new ConflictObjectResult(response),
            _ => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        };

    private bool TryCreateReference(string routeCaseId, WebEffectReconciliationCaseReference request, out GovernedLoopEffectReconciliationCaseReference? reference)
    {
        reference = null;
        if (request is null || string.IsNullOrWhiteSpace(routeCaseId) || !string.Equals(routeCaseId, request.CaseId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            reference = new GovernedLoopEffectReconciliationCaseReference(routeCaseId, request.CaseVersion, request.ContentHash ?? string.Empty, request.BindingHash ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool IsWorkspaceInitialized() => _runtime.IsWorkspaceInitialized;

    private ConflictObjectResult WorkspaceNotInitialized()
        => Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before inspecting or changing effect reconciliation." });

    private ObjectResult RuntimeUnavailable()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "effect_reconciliation_unavailable", detail = "The canonical reconciliation runtime is unavailable." });
}

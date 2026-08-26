using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>
/// Exposes authenticated, no-store HTTP authoring for the system loop and bounded custom-loop definitions.
/// </summary>
/// <remarks>
/// All operations require an initialized workspace. The system default is readable but immutable;
/// custom mutations use caller-provided operation identities and optimistic definition versions.
/// </remarks>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/loops")]
public sealed class LoopsController : ControllerBase
{
    private readonly ILoopReceiptRetentionFacade _receiptRetention;
    private readonly WebAgentRuntimeHost _host;

    /// <summary>
    /// Initializes the loop-authoring controller.
    /// </summary>
    /// <param name="receiptRetention">The Startup-only facade that owns receipt posture and governed cleanup attribution.</param>
    /// <param name="host">The Web host used for workspace and runtime-model status.</param>
    public LoopsController(ILoopReceiptRetentionFacade receiptRetention, WebAgentRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(receiptRetention);
        ArgumentNullException.ThrowIfNull(host);

        _receiptRetention = receiptRetention;
        _host = host;
    }

    /// <summary>
    /// Lists the immutable default loop, custom-loop catalog, limits, and effective custom-loop model.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the catalog read.</param>
    /// <returns>HTTP 200 with the catalog, or HTTP 409 when the workspace is not initialized.</returns>
    [HttpGet]
    public async Task<ActionResult<LoopAuthoringCatalog>> List(CancellationToken cancellationToken)
    {
        if (!IsWorkspaceInitialized())
        {
            return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before managing loops." });
        }

        var catalog = await _host.UseLoopAuthoringAsync(loops => loops.GetCatalogAsync(cancellationToken), cancellationToken);
        return Ok(catalog with { RuntimeModel = _host.GetCustomLoopModel() });
    }

    /// <summary>
    /// Gets the canonical read-only default conversation loop.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the definition read.</param>
    /// <returns>HTTP 200 with the system-loop graph and policy, or HTTP 409 when the workspace is not initialized.</returns>
    [HttpGet("default-conversation")]
    public async Task<ActionResult<SystemLoopDefinitionSnapshot>> GetSystemDefault(CancellationToken cancellationToken)
    {
        if (!IsWorkspaceInitialized())
        {
            return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before managing loops." });
        }

        return Ok((await _host.UseLoopAuthoringAsync(loops => loops.GetCatalogAsync(cancellationToken), cancellationToken)).SystemDefault);
    }

    /// <summary>
    /// Gets one custom-loop definition.
    /// </summary>
    /// <param name="loopId">The custom artifact identifier.</param>
    /// <param name="cancellationToken">The token used to cancel the definition read.</param>
    /// <returns>
    /// HTTP 200 with the definition, HTTP 400 for an invalid custom identifier, HTTP 404 when a
    /// valid custom identifier is absent, or HTTP 409 when the workspace is not initialized.
    /// </returns>
    [HttpGet("{loopId}")]
    public async Task<ActionResult<LoopDefinitionSnapshot>> Get(string loopId, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceInitialized())
        {
            return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before managing loops." });
        }

        try
        {
            var definition = await _host.UseLoopAuthoringAsync(loops => loops.GetAsync(loopId, cancellationToken), cancellationToken);
            return definition is null ? NotFound() : Ok(definition);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_loop_id", detail = "The loop id is not a valid artifact identifier." });
        }
    }

    /// <summary>
    /// Atomically creates the first durable version of an explicit client-side custom-loop draft.
    /// </summary>
    /// <param name="request">The caller-owned idempotency identity and complete first-save definition.</param>
    /// <param name="cancellationToken">The token used to cancel authoring.</param>
    /// <returns>
    /// HTTP 201 for a newly created definition; otherwise the facade response projected as HTTP
    /// 200, 400, 404, 409, 500, or 503 according to its durable status.
    /// </returns>
    [HttpPost]
    public async Task<ActionResult<LoopAuthoringResponse>> Create([FromBody] CreateLoopRequest request, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceInitialized())
        {
            return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before managing loops." });
        }

        if (request.Definition is null)
        {
            return Project(new LoopAuthoringResponse(
                "Invalid",
                false,
                null,
                [new LoopValidationError("definition_required", "definition", "The complete first-save definition is required.")],
                null,
                "The first-save definition is required."));
        }

        var response = await _host.UseLoopAuthoringAsync(loops => loops.CreateAsync(request.OperationId, request.Definition, cancellationToken), cancellationToken);
        return response.Status == "Created"
            ? CreatedAtAction(nameof(Get), new { loopId = response.Definition!.Id }, response)
            : Project(response);
    }

    /// <summary>
    /// Replaces one custom-loop definition using optimistic concurrency.
    /// </summary>
    /// <param name="loopId">The custom artifact identifier.</param>
    /// <param name="request">The expected version, idempotency identity, and complete replacement definition.</param>
    /// <param name="cancellationToken">The token used to cancel authoring.</param>
    /// <returns>
    /// The facade response projected as HTTP 200, 400, 404, 409, 500, or 503. The immutable
    /// default loop and an uninitialized workspace return HTTP 409.
    /// </returns>
    [HttpPut("{loopId}")]
    public async Task<ActionResult<LoopAuthoringResponse>> Update(string loopId, [FromBody] UpdateLoopRequest request, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceInitialized())
        {
            return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before managing loops." });
        }

        if (string.Equals(loopId, "default-conversation", StringComparison.Ordinal))
        {
            return Conflict(new { error = "system_loop_locked", detail = "The default conversation loop is read-only." });
        }

        return Project(await _host.UseLoopAuthoringAsync(loops => loops.UpdateAsync(loopId, request.ExpectedDefinitionVersion, request.OperationId, request.Definition, cancellationToken), cancellationToken));
    }

    /// <summary>
    /// Deletes one custom-loop definition using optimistic concurrency.
    /// </summary>
    /// <param name="loopId">The custom artifact identifier.</param>
    /// <param name="request">The expected version and idempotency identity.</param>
    /// <param name="cancellationToken">The token used to cancel authoring.</param>
    /// <returns>
    /// The facade response projected as HTTP 200, 400, 404, 409, 500, or 503. The immutable
    /// default loop and an uninitialized workspace return HTTP 409.
    /// </returns>
    [HttpDelete("{loopId}")]
    public async Task<ActionResult<LoopAuthoringResponse>> Delete(string loopId, [FromBody] DeleteLoopRequest request, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceInitialized())
        {
            return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before managing loops." });
        }

        if (string.Equals(loopId, "default-conversation", StringComparison.Ordinal))
        {
            return Conflict(new { error = "system_loop_locked", detail = "The default conversation loop is read-only." });
        }

        return Project(await _host.UseLoopAuthoringAsync(loops => loops.DeleteAsync(loopId, request.ExpectedDefinitionVersion, request.OperationId, cancellationToken), cancellationToken));
    }

    /// <summary>
    /// Gets bounded custom-loop receipt-retention posture without exposing protocol artifacts.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the retention inspection.</param>
    /// <returns>HTTP 200 with safe retention posture, or HTTP 409 when the workspace is not initialized.</returns>
    [HttpGet("receipt-retention")]
    public async Task<ActionResult<LoopReceiptRetentionPostureSnapshot>> GetReceiptRetention(CancellationToken cancellationToken)
    {
        if (!IsWorkspaceInitialized())
        {
            return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before inspecting custom-loop receipt retention." });
        }

        return Ok(await _receiptRetention.GetPostureAsync(cancellationToken));
    }

    /// <summary>
    /// Executes one caller-requested, server-attributed, policy-bounded custom-loop receipt cleanup.
    /// </summary>
    /// <param name="request">The class, idempotency identity, and bounded cleanup limits.</param>
    /// <param name="cancellationToken">The token used to cancel cleanup before its durable terminal boundary.</param>
    /// <returns>HTTP 200 for a terminal result, or a safe error projection when cleanup cannot proceed.</returns>
    [HttpPost("receipt-retention/cleanup")]
    public async Task<ActionResult<LoopReceiptCleanupResponse>> CleanupReceiptRetention([FromBody] LoopReceiptCleanupInput request, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceInitialized())
        {
            return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before cleaning custom-loop receipt retention." });
        }

        var response = await _receiptRetention.CleanupAsync(request, cancellationToken);
        return response.Status switch
        {
            "Pruned" or "Replayed" or "NothingEligible" or "CommittedWithAuditWarning" => Ok(response),
            "Invalid" => BadRequest(response),
            "AuditUnavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
            "OperationInProgress" or "QuotaExhausted" or "CleanupConflict" or "Corrupt" or "Degraded" => Conflict(response),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response)
        };
    }

    private ActionResult<LoopAuthoringResponse> Project(LoopAuthoringResponse response)
    {
        return response.Status switch
        {
            "Created" or "Updated" or "Deleted" or "Replayed" or "CommittedWithAuditWarning" => Ok(response),
            "Invalid" => BadRequest(response),
            "Conflict" or "LimitExceeded" or "ActiveRunExists" => Conflict(response),
            "NotFound" => NotFound(response),
            "AuditUnavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response)
        };
    }

    private bool IsWorkspaceInitialized() => _host.GetStatus().Initialized;
}

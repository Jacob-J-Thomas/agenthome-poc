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
    private readonly LoopAuthoringFacade _loops;
    private readonly WebAgentRuntimeHost _host;

    /// <summary>
    /// Initializes the loop-authoring controller.
    /// </summary>
    /// <param name="loops">The reusable authoring facade for durable definitions.</param>
    /// <param name="host">The Web host used for workspace and runtime-model status.</param>
    public LoopsController(LoopAuthoringFacade loops, WebAgentRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(host);

        _loops = loops;
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

        var catalog = await _loops.GetCatalogAsync(cancellationToken);
        return Ok(catalog with { RuntimeModel = _host.GetCustomLoopModel() });
    }

    /// <summary>
    /// Gets the default loop or one custom-loop definition.
    /// </summary>
    /// <param name="loopId">The reserved <c>default-conversation</c> identity or a custom artifact identifier.</param>
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

        if (string.Equals(loopId, "default-conversation", StringComparison.Ordinal))
        {
            return Ok((await _loops.GetCatalogAsync(cancellationToken)).SystemDefault);
        }

        try
        {
            var definition = await _loops.GetAsync(loopId, cancellationToken);
            return definition is null ? NotFound() : Ok(definition);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_loop_id", detail = "The loop id is not a valid artifact identifier." });
        }
    }

    /// <summary>
    /// Creates a new first-wave custom-loop definition.
    /// </summary>
    /// <param name="request">The caller-owned idempotency identity.</param>
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

        var response = await _loops.CreateAsync(request.OperationId, cancellationToken);
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

        return Project(await _loops.UpdateAsync(loopId, request.ExpectedDefinitionVersion, request.OperationId, request.Definition, cancellationToken));
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

        return Project(await _loops.DeleteAsync(loopId, request.ExpectedDefinitionVersion, request.OperationId, cancellationToken));
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

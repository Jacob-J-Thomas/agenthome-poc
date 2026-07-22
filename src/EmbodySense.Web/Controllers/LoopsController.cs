using EmbodySense.Core.Startup.Loops;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/loops")]
public sealed class LoopsController : ControllerBase
{
    private readonly LoopAuthoringFacade _loops;
    private readonly WebAgentRuntimeHost _host;

    public LoopsController(LoopAuthoringFacade loops, WebAgentRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(host);

        _loops = loops;
        _host = host;
    }

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

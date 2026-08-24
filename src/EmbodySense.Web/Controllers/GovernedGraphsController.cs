using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>Projects the shared catalog, immutable graph history, and role-bound authoring facade.</summary>
/// <remarks>Actor, surface, workspace, role authority, validation, lifecycle policy, and persistence remain server-owned.</remarks>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/governed-graphs")]
public sealed class GovernedGraphsController : ControllerBase
{
    private readonly WebAgentRuntimeHost _host;

    /// <summary>Creates a thin authenticated projection over the retained runtime.</summary>
    /// <param name="host">The process-wide runtime owner.</param>
    public GovernedGraphsController(WebAgentRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Reads the exact server executable-node catalog and safe active role choices.</summary>
    [HttpGet("catalog")]
    public async Task<ActionResult<GovernedLoopGraphCatalogResponse>> ReadCatalog(
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        var response = await _host.ReadGovernedLoopGraphCatalogAsync(cancellationToken);
        return response.Status == "available"
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

    /// <summary>Canonicalizes non-authoritative retry-policy authoring values and returns finite server-owned bounds.</summary>
    [HttpPost("retry-preview")]
    public async Task<ActionResult<GovernedLoopRetryPolicyPreviewResponse>> PreviewRetryPolicy(
        [FromBody] GovernedLoopRetryPolicyPreviewInput? input,
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }
        if (input is null)
        {
            return BadRequest(new { error = "retry_policy_preview_required", detail = "Bounded retry authoring intent is required." });
        }

        var response = await _host.PreviewGovernedLoopRetryPolicyAsync(input, cancellationToken);
        return response.Status == "valid" ? Ok(response) : BadRequest(response);
    }

    /// <summary>Reads one exact immutable graph aggregate by canonical identity.</summary>
    [HttpGet("detail")]
    public async Task<ActionResult<GovernedLoopGraphReadResponse>> Read(
        [FromQuery] string graphId,
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        return Project(await _host.ReadGovernedLoopGraphAsync(graphId, cancellationToken));
    }

    /// <summary>Applies one exact optimistic lifecycle mutation without caller-supplied actor or authority evidence.</summary>
    [HttpPost("mutate")]
    public async Task<ActionResult<GovernedLoopGraphMutationResponse>> Mutate(
        [FromBody] GovernedLoopGraphMutationInput? input,
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }
        if (input is null)
        {
            return BadRequest(new { error = "governed_graph_mutation_required", detail = "Exact graph content and lifecycle evidence are required." });
        }

        var response = await _host.MutateGovernedLoopGraphAsync(input, cancellationToken);
        return response.Status switch
        {
            "committed" or "replayed" => Ok(response),
            "invalid" or "validation-rejected" or "limit-exceeded" => BadRequest(response),
            "not-found" => NotFound(response),
            "conflict" or "publication-rejected" or "ambiguous" or "unauthorized" => Conflict(response),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
        };
    }

    private ActionResult<GovernedLoopGraphReadResponse> Project(GovernedLoopGraphReadResponse response)
        => response.Status switch
        {
            "ready" => Ok(response),
            "not-found" => NotFound(response),
            "ambiguous" => Conflict(response),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
        };

    private ObjectResult WorkspaceNotInitialized()
        => Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before authoring governed graphs." });

    private bool IsWorkspaceInitialized() => _host.GetStatus().Initialized;
}

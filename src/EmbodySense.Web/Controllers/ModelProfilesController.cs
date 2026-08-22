using EmbodySense.Core.Startup.Inference.Profiles.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>Projects the shared bounded safe model-profile catalog.</summary>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/model-profiles")]
public sealed class ModelProfilesController : ControllerBase
{
    private readonly WebAgentRuntimeHost _host;

    /// <summary>Creates the thin authenticated Web projection.</summary>
    public ModelProfilesController(WebAgentRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Reads one exact current profile page and configured default.</summary>
    [HttpGet]
    public async Task<ActionResult<ModelProfileCatalogResponse>> Read(
        [FromQuery] string? startAfterId = null,
        [FromQuery] int maximumCount = 50,
        CancellationToken cancellationToken = default)
    {
        if (!_host.GetStatus().Initialized)
        {
            return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before reading model profiles." });
        }

        var response = await _host.ReadModelProfilesAsync(startAfterId, maximumCount, cancellationToken);
        return response.Status switch
        {
            "available" => Ok(response),
            "invalid" or "limitexceeded" => BadRequest(response),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
        };
    }

    /// <summary>Recomputes a non-granting effective routing preview from server-owned catalog evidence.</summary>
    [HttpPost("preview")]
    public async Task<ActionResult<ModelProfileRoutingPreviewResponse>> Preview(
        [FromBody] ModelProfileRoutingPreviewInput input,
        CancellationToken cancellationToken = default)
    {
        if (!_host.GetStatus().Initialized)
        {
            return Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before previewing model routing." });
        }
        var response = await _host.PreviewModelRoutingAsync(input, cancellationToken);
        return response.Status switch
        {
            "eligible" or "ineligible" => Ok(response),
            "invalid" => BadRequest(response),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response),
        };
    }
}

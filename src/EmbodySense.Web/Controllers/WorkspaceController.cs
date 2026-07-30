using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>
/// Exposes explicit workspace initialization to an authenticated local browser.
/// </summary>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[Route("api/workspace")]
public sealed class WorkspaceController : ControllerBase
{
    private readonly WebAgentRuntimeHost _host;

    /// <summary>
    /// Initializes the workspace endpoint.
    /// </summary>
    /// <param name="host">The Web host that owns workspace initialization.</param>
    public WorkspaceController(WebAgentRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
    }

    /// <summary>
    /// Idempotently creates the Web workspace scaffold and returns its resulting status.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel initialization before completion.</param>
    /// <returns>HTTP 200 with the post-initialization status.</returns>
    [HttpPost("init")]
    public async Task<ActionResult<WebStatus>> Initialize(CancellationToken cancellationToken)
    {
        return Ok(await _host.InitializeWorkspaceAsync(cancellationToken));
    }
}

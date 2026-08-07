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
    private readonly IWebClientNotifier _notifier;

    /// <summary>
    /// Initializes the workspace endpoint.
    /// </summary>
    /// <param name="host">The Web host that owns workspace initialization.</param>
    /// <param name="notifier">The authenticated client-status publication boundary.</param>
    public WorkspaceController(WebAgentRuntimeHost host, IWebClientNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(notifier);

        _host = host;
        _notifier = notifier;
    }

    /// <summary>
    /// Idempotently creates the Web workspace scaffold and returns its resulting status.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel initialization before completion.</param>
    /// <returns>HTTP 200 with the post-initialization status.</returns>
    [HttpPost("init")]
    public async Task<ActionResult<WebStatus>> Initialize(CancellationToken cancellationToken)
    {
        var status = await _host.InitializeWorkspaceAsync(cancellationToken);
        await _notifier.StatusChangedAsync(status);
        return Ok(status);
    }
}

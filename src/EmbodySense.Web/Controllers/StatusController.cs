using EmbodySense.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>
/// Exposes an anonymous, read-only status snapshot for local browser startup.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/status")]
public sealed class StatusController : ControllerBase
{
    private readonly WebAgentRuntimeHost _host;

    /// <summary>
    /// Initializes the status endpoint.
    /// </summary>
    /// <param name="host">The Web host that projects workspace status.</param>
    public StatusController(WebAgentRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
    }

    /// <summary>
    /// Gets current Web binding and workspace-initialization status without creating a runtime.
    /// </summary>
    /// <returns>HTTP 200 with the current status snapshot.</returns>
    [HttpGet]
    public ActionResult<WebStatus> Get()
    {
        return Ok(_host.GetStatus());
    }
}

using EmbodySense.Core.Startup.Configuration.Models;
using EmbodySense.Core.Startup.Configuration;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>
/// Projects read-only workspace and Codex runtime configuration to an authenticated local browser.
/// </summary>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[Route("api/configuration")]
public sealed class ConfigurationController : ControllerBase
{
    private readonly WebAgentRuntimeHost _host;

    /// <summary>
    /// Initializes the configuration endpoint.
    /// </summary>
    /// <param name="host">The Web runtime host that owns configuration discovery.</param>
    public ConfigurationController(WebAgentRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
    }

    /// <summary>
    /// Reads the effective workspace and cached Codex runtime compatibility configuration.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel compatibility or configuration reads.</param>
    /// <returns>HTTP 200 with the effective configuration snapshot.</returns>
    [HttpGet]
    public async Task<ActionResult<WorkspaceConfigurationSnapshot>> Get(CancellationToken cancellationToken)
    {
        return Ok(await _host.GetConfigurationAsync(cancellationToken));
    }
}

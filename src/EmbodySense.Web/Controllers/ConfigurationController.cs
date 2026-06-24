using EmbodySense.Core.Startup.Configuration;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[Route("api/configuration")]
public sealed class ConfigurationController : ControllerBase
{
    private readonly WebAgentRuntimeHost _host;

    public ConfigurationController(WebAgentRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
    }

    [HttpGet]
    public async Task<ActionResult<WorkspaceConfigurationSnapshot>> Get(CancellationToken cancellationToken)
    {
        return Ok(await _host.GetConfigurationAsync(cancellationToken));
    }
}

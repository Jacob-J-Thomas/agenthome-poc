using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[Route("api/workspace")]
public sealed class WorkspaceController : ControllerBase
{
    private readonly WebAgentRuntimeHost _host;

    public WorkspaceController(WebAgentRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
    }

    [HttpPost("init")]
    public async Task<ActionResult<WebStatus>> Initialize(CancellationToken cancellationToken)
    {
        return Ok(await _host.InitializeWorkspaceAsync(cancellationToken));
    }
}

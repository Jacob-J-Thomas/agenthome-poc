using EmbodySense.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

[ApiController]
[Route("api/status")]
public sealed class StatusController : ControllerBase
{
    private readonly WebAgentRuntimeHost _host;

    public StatusController(WebAgentRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
    }

    [HttpGet]
    public ActionResult<WebStatus> Get()
    {
        return Ok(_host.GetStatus());
    }
}

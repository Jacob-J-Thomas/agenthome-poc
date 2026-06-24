using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

[ApiController]
[Route("api/session")]
public sealed class SessionController : ControllerBase
{
    private readonly WebSessionSecurity _sessionSecurity;

    public SessionController(WebSessionSecurity sessionSecurity)
    {
        ArgumentNullException.ThrowIfNull(sessionSecurity);

        _sessionSecurity = sessionSecurity;
    }

    [HttpGet]
    public ActionResult<WebSessionInfo> Get()
    {
        return Ok(new WebSessionInfo(_sessionSecurity.Token));
    }
}

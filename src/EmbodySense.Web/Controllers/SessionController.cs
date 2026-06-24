using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

[ApiController]
[AllowAnonymous]
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
        // TODO: Replace this anonymous bootstrap token with an explicit local pairing flow before treating browser auth as hardened.
        return Ok(new WebSessionInfo(_sessionSecurity.Token));
    }
}

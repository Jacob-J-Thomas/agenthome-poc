using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>
/// Bootstraps an anonymous localhost browser with the process-local session bearer token.
/// </summary>
/// <remarks>
/// This endpoint validates the request host and optional origin before returning the token. It is
/// a POC localhost bootstrap boundary rather than a hardened user-pairing or remote-authentication flow.
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/session")]
public sealed class SessionController : ControllerBase
{
    private readonly WebSessionSecurity _sessionSecurity;

    /// <summary>
    /// Initializes the session bootstrap endpoint.
    /// </summary>
    /// <param name="sessionSecurity">The process-local token and localhost policy.</param>
    public SessionController(WebSessionSecurity sessionSecurity)
    {
        ArgumentNullException.ThrowIfNull(sessionSecurity);

        _sessionSecurity = sessionSecurity;
    }

    /// <summary>
    /// Returns the process-local bearer token to an allowed localhost host and origin.
    /// </summary>
    /// <returns>HTTP 200 with the opaque token, or HTTP 401 when host or origin validation fails.</returns>
    [HttpGet]
    public ActionResult<WebSessionInfo> Get()
    {
        if (!_sessionSecurity.IsHostAllowed(Request.Host) || !_sessionSecurity.IsOriginAllowed(Request))
        {
            return Unauthorized();
        }

        return Ok(new WebSessionInfo(_sessionSecurity.Token));
    }
}

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EmbodySense.Web.Services;

/// <summary>
/// Authenticates localhost HTTP and SignalR requests with the process-local opaque session token.
/// </summary>
public sealed class WebSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly WebSessionSecurity _sessionSecurity;

    /// <summary>
    /// Initializes the local-session authentication handler.
    /// </summary>
    /// <param name="options">The authentication scheme options monitor.</param>
    /// <param name="logger">The authentication logger factory.</param>
    /// <param name="encoder">The URL encoder required by the ASP.NET authentication base class.</param>
    /// <param name="sessionSecurity">The process-local host, origin, and token policy.</param>
    public WebSessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        WebSessionSecurity sessionSecurity)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(sessionSecurity);

        _sessionSecurity = sessionSecurity;
    }

    /// <summary>
    /// Enforces localhost host and origin constraints before authenticating the opaque session token.
    /// </summary>
    /// <returns>
    /// Failure for a disallowed host or origin, no result for a missing or invalid token, or an
    /// authenticated <c>localhost-web-user</c> ticket for a valid token.
    /// </returns>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_sessionSecurity.IsHostAllowed(Request.Host))
        {
            return Task.FromResult(AuthenticateResult.Fail("The EmbodySense Web UI only accepts localhost requests."));
        }

        if (!_sessionSecurity.IsOriginAllowed(Request))
        {
            return Task.FromResult(AuthenticateResult.Fail("The EmbodySense Web UI only accepts local same-port origins."));
        }

        if (!_sessionSecurity.HasValidToken(Request))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "localhost-web-user")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

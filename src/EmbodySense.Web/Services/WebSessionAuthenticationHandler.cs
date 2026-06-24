using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EmbodySense.Web.Services;

public sealed class WebSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly WebSessionSecurity _sessionSecurity;

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

using System.Security.Cryptography;

namespace EmbodySense.Web.Services;

/// <summary>
/// Owns the process-local Web session credential and localhost host and origin validation rules.
/// </summary>
/// <remarks>
/// The policy accepts loopback host spellings only. Requests without an <c>Origin</c> header remain
/// eligible for token authentication; requests with an origin must use a loopback host and the
/// request port when the request host specifies one. Browser requests authenticate with an HttpOnly
/// same-site cookie; the explicit session header remains available to non-browser local clients.
/// </remarks>
public sealed class WebSessionSecurity
{
    /// <summary>
    /// Names the HttpOnly browser cookie that carries the local session credential.
    /// </summary>
    public const string CookieName = "EmbodySense.Session";

    /// <summary>
    /// Names the HTTP header that carries the local session token.
    /// </summary>
    public const string HeaderName = "X-EmbodySense-Session";
    private static readonly HashSet<string> _localHosts = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "::1" };

    /// <summary>
    /// Initializes a session policy with a cryptographically random 256-bit token.
    /// </summary>
    public WebSessionSecurity()
        : this(CreateToken(), Guid.NewGuid().ToString("N"))
    {
    }

    /// <summary>
    /// Initializes a session policy with an explicit opaque token.
    /// </summary>
    /// <param name="token">The nonblank token required for authenticated requests.</param>
    public WebSessionSecurity(string token)
        : this(token, Guid.NewGuid().ToString("N"))
    {
    }

    /// <summary>
    /// Initializes a session policy with explicit credential and process-generation values.
    /// </summary>
    /// <param name="token">The nonblank credential required for authenticated requests.</param>
    /// <param name="generationId">The nonblank, non-secret process generation identifier.</param>
    public WebSessionSecurity(string token, string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);

        Token = token;
        GenerationId = generationId;
    }

    /// <summary>
    /// Gets the process-local opaque bearer token.
    /// </summary>
    public string Token { get; }

    /// <summary>
    /// Gets the non-secret identifier for this Web host process generation.
    /// </summary>
    public string GenerationId { get; }

    /// <summary>
    /// Determines whether a request host is one of the accepted loopback spellings.
    /// </summary>
    /// <param name="host">The request host; any IPv6 brackets are ignored for comparison.</param>
    /// <returns><see langword="true"/> for <c>127.0.0.1</c>, <c>localhost</c>, or <c>::1</c>.</returns>
    public bool IsHostAllowed(HostString host)
    {
        var normalizedHost = NormalizeHost(host.Host);
        return _localHosts.Contains(normalizedHost);
    }

    /// <summary>
    /// Validates an optional request origin against the loopback host set and request port.
    /// </summary>
    /// <param name="request">The HTTP request to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when no origin is supplied or an absolute loopback origin uses the
    /// request port when that port is explicit; otherwise <see langword="false"/>.
    /// </returns>
    public bool IsOriginAllowed(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        if (!_localHosts.Contains(NormalizeHost(originUri.Host)))
        {
            return false;
        }

        return request.Host.Port is null || originUri.Port == request.Host.Port;
    }

    /// <summary>
    /// Validates the session credential from the HttpOnly cookie or explicit local-client header.
    /// </summary>
    /// <param name="request">The HTTP or SignalR request to authenticate.</param>
    /// <returns><see langword="true"/> only for an ordinal exact token match.</returns>
    public bool HasValidToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cookieToken = request.Cookies[CookieName];
        if (string.Equals(cookieToken, Token, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(request.Headers[HeaderName].ToString(), Token, StringComparison.Ordinal);
    }

    private static string NormalizeHost(string host)
    {
        return host.Trim().Trim('[', ']');
    }

    private static string CreateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }
}

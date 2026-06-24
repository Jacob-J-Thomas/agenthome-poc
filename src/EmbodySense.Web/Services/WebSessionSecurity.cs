using System.Security.Cryptography;

namespace EmbodySense.Web.Services;

public sealed class WebSessionSecurity
{
    public const string HeaderName = "X-EmbodySense-Session";
    private static readonly HashSet<string> LocalHosts = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "::1" };

    public WebSessionSecurity()
        : this(CreateToken())
    {
    }

    public WebSessionSecurity(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        Token = token;
    }

    public string Token { get; }

    public bool RequiresToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method);
    }

    public bool IsAuthorized(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsHostAllowed(request.Host))
        {
            return false;
        }

        if (!IsOriginAllowed(request))
        {
            return false;
        }

        return !RequiresToken(request) || string.Equals(request.Headers[HeaderName].ToString(), Token, StringComparison.Ordinal);
    }

    public bool IsHostAllowed(HostString host)
    {
        var normalizedHost = NormalizeHost(host.Host);
        return LocalHosts.Contains(normalizedHost);
    }

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

        if (!LocalHosts.Contains(NormalizeHost(originUri.Host)))
        {
            return false;
        }

        return request.Host.Port is null || originUri.Port == request.Host.Port;
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

using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Clients.Capabilities;

/// <summary>Reads a bounded artifact from one canonical HTTPS response without redirects or credential-bearing URIs.</summary>
public sealed class RemoteCapabilityArtifactSource : IRemoteCapabilityArtifactSource, IDisposable
{
    private readonly IRemoteCapabilityArtifactTransport _transport;
    private readonly IReadOnlySet<string> _allowedOrigins;

    /// <summary>Creates a remote source with a handler owned by this boundary and automatic redirects disabled.</summary>
    public RemoteCapabilityArtifactSource(IEnumerable<string> allowedOrigins)
    {
        _transport = new NoRedirectRemoteCapabilityArtifactTransport();
        _allowedOrigins = CreateAllowedOrigins(allowedOrigins);
    }

    /// <summary>Creates a remote source over one server-owned transport that is contractually incapable of following redirects.</summary>
    public RemoteCapabilityArtifactSource(IRemoteCapabilityArtifactTransport transport, IEnumerable<string> allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _allowedOrigins = CreateAllowedOrigins(allowedOrigins);
    }

    /// <inheritdoc />
    public async Task<CapabilityArtifactContent> ReadAsync(CapabilityArtifactSourceReference source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != CapabilityArtifactSourceKind.Remote || !Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.Equals(uri.AbsoluteUri, source.Uri, StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical credential-free HTTPS source is required.", nameof(source));
        }
        if (!_allowedOrigins.Contains(Origin(uri)))
        {
            throw new UnauthorizedAccessException("The remote artifact source origin is not allowed by server-owned policy.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _transport.SendExactAsync(request, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException("Remote artifact redirects are refused before a Location target can be requested.");
        }
        if (response.RequestMessage?.RequestUri is not { } finalUri || finalUri != uri)
        {
            throw new HttpRequestException("Remote artifact redirects are refused.");
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is <= 0 or > CapabilityArtifactManifestValidator.MaximumArtifactBytes)
        {
            throw new HttpRequestException("The remote artifact is empty or exceeds the intake bound.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(response.Content.Headers.ContentLength is > 0 and <= int.MaxValue ? (int)response.Content.Headers.ContentLength.Value : 0);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (output.Length + count > CapabilityArtifactManifestValidator.MaximumArtifactBytes)
            {
                throw new HttpRequestException("The remote artifact exceeds the intake bound.");
            }
            output.Write(buffer, 0, count);
        }

        if (output.Length == 0)
        {
            throw new HttpRequestException("The remote artifact is empty.");
        }
        return new CapabilityArtifactContent(output.ToArray());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _transport.Dispose();
    }

    private static IReadOnlySet<string> CreateAllowedOrigins(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(Origin(uri), value, StringComparison.Ordinal))
            {
                throw new ArgumentException("Remote artifact origins must be canonical HTTPS origins.", nameof(values));
            }
            origins.Add(value);
        }
        if (origins.Count == 0)
        {
            throw new ArgumentException("At least one server-owned remote artifact origin is required.", nameof(values));
        }
        return origins;
    }

    private static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority);
}

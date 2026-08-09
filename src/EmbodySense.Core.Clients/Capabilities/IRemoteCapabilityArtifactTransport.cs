namespace EmbodySense.Core.Clients.Capabilities;

/// <summary>Represents server-owned HTTPS transport that sends one exact request without following redirects.</summary>
/// <remarks>Implementations must return redirect responses as responses; they must not issue a follow-up request to a Location target.</remarks>
public interface IRemoteCapabilityArtifactTransport : IDisposable
{
    /// <summary>Sends one exact request and returns its response without automatic redirect handling.</summary>
    Task<HttpResponseMessage> SendExactAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}

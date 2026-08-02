namespace EmbodySense.Core.Clients.Capabilities;

/// <summary>Owns the production HTTP handler that refuses redirects before a follow-up request can be issued.</summary>
internal sealed class NoRedirectRemoteCapabilityArtifactTransport : IRemoteCapabilityArtifactTransport
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true);

    public Task<HttpResponseMessage> SendExactAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public void Dispose() => _client.Dispose();
}

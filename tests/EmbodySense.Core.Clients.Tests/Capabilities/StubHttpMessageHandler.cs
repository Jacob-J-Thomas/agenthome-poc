using System.Net;
using EmbodySense.Core.Clients.Capabilities;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

internal sealed class StubRemoteCapabilityArtifactTransport : IRemoteCapabilityArtifactTransport
{
    internal Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = request => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent("artifact"u8.ToArray()) };
    internal List<Uri> RequestedUris { get; } = [];

    public Task<HttpResponseMessage> SendExactAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        RequestedUris.Add(request.RequestUri!);
        return Task.FromResult(Handler(request));
    }

    public void Dispose()
    {
    }
}

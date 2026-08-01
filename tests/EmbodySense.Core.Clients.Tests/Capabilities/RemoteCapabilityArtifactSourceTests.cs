using System.Net;
using System.Net.Http.Headers;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Clients.Capabilities;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

public sealed class RemoteCapabilityArtifactSourceTests
{
    [Fact]
    public async Task Exact_https_response_is_read_with_a_hard_byte_bound()
    {
        using var transport = new StubRemoteCapabilityArtifactTransport();
        var source = new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Remote, "https://example.test/artifact", "rev", CapabilityArtifactUpdatePolicy.Pinned);

        var result = await new RemoteCapabilityArtifactSource(transport, ["https://example.test"]).ReadAsync(source);

        Assert.Equal("artifact"u8.ToArray(), result.ToArray());
    }

    [Fact]
    public async Task Redirect_credentials_and_oversized_content_fail_closed()
    {
        using var transport = new StubRemoteCapabilityArtifactTransport
        {
            Handler = request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.test/artifact"), Content = new ByteArrayContent([1]) };
                response.Content.Headers.ContentLength = EmbodySense.Core.Application.Capabilities.CapabilityArtifactManifestValidator.MaximumArtifactBytes + 1L;
                return response;
            }
        };
        var remote = new RemoteCapabilityArtifactSource(transport, ["https://example.test"]);

        await Assert.ThrowsAsync<HttpRequestException>(() => remote.ReadAsync(new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Remote, "https://example.test/artifact", "rev", CapabilityArtifactUpdatePolicy.Pinned)));
        await Assert.ThrowsAsync<ArgumentException>(() => remote.ReadAsync(new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Remote, "https://user:password@example.test/artifact", "rev", CapabilityArtifactUpdatePolicy.Pinned)));
    }

    [Fact]
    public async Task Redirect_response_never_issues_a_request_to_its_location_target()
    {
        using var transport = new StubRemoteCapabilityArtifactTransport
        {
            Handler = request => new HttpResponseMessage(HttpStatusCode.Found)
            {
                RequestMessage = request,
                Headers = { Location = new Uri("https://internal.example.test/private") }
            }
        };
        using var remote = new RemoteCapabilityArtifactSource(transport, ["https://example.test"]);
        var source = new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Remote, "https://example.test/artifact", "rev", CapabilityArtifactUpdatePolicy.Pinned);

        await Assert.ThrowsAsync<HttpRequestException>(() => remote.ReadAsync(source));

        Assert.Equal([new Uri("https://example.test/artifact")], transport.RequestedUris);
        Assert.DoesNotContain(new Uri("https://internal.example.test/private"), transport.RequestedUris);
    }

    [Fact]
    public async Task Empty_and_chunked_oversized_responses_fail_closed()
    {
        using var transport = new StubRemoteCapabilityArtifactTransport();
        var remote = new RemoteCapabilityArtifactSource(transport, ["https://example.test"]);
        var source = new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Remote, "https://example.test/artifact", "rev", CapabilityArtifactUpdatePolicy.Pinned);
        transport.Handler = request => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([]) };

        await Assert.ThrowsAsync<HttpRequestException>(() => remote.ReadAsync(source));

        transport.Handler = request => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new UnknownLengthContent(EmbodySense.Core.Application.Capabilities.CapabilityArtifactManifestValidator.MaximumArtifactBytes + 1) };
        await Assert.ThrowsAsync<HttpRequestException>(() => remote.ReadAsync(source));

        transport.Handler = request => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new UnknownLengthContent(0) };
        await Assert.ThrowsAsync<HttpRequestException>(() => remote.ReadAsync(source));
    }

    [Fact]
    public async Task Non_success_and_unknown_length_nonempty_responses_have_honest_outcomes()
    {
        using var transport = new StubRemoteCapabilityArtifactTransport();
        using var remote = new RemoteCapabilityArtifactSource(transport, ["https://example.test"]);
        var source = new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Remote, "https://example.test/artifact", "rev", CapabilityArtifactUpdatePolicy.Pinned);
        transport.Handler = request => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new ByteArrayContent("not-found"u8.ToArray()) };

        await Assert.ThrowsAsync<HttpRequestException>(() => remote.ReadAsync(source));

        transport.Handler = request => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new UnknownLengthContent(3) };
        var content = await remote.ReadAsync(source);

        Assert.Equal(new byte[3], content.ToArray());
    }

    [Fact]
    public async Task Server_owned_origin_policy_blocks_unapproved_source_before_transport()
    {
        using var transport = new StubRemoteCapabilityArtifactTransport();
        var remote = new RemoteCapabilityArtifactSource(transport, ["https://allowed.test"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => remote.ReadAsync(new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Remote, "https://blocked.test/artifact", "rev", CapabilityArtifactUpdatePolicy.Pinned)));
    }

    [Fact]
    public async Task Default_owned_transport_and_server_origin_configuration_fail_closed()
    {
        using var transport = new StubRemoteCapabilityArtifactTransport();

        Assert.Throws<ArgumentNullException>(() => new RemoteCapabilityArtifactSource(transport, null!));
        Assert.Throws<ArgumentException>(() => new RemoteCapabilityArtifactSource(transport, ["http://allowed.test"]));
        Assert.Throws<ArgumentException>(() => new RemoteCapabilityArtifactSource(transport, []));

        using var source = new RemoteCapabilityArtifactSource(["https://allowed.test"]);
        var nonCanonicalSource = new CapabilityArtifactSourceReference(
            CapabilityArtifactSourceKind.Remote, "https://allowed.test/artifact?unexpected", "rev", CapabilityArtifactUpdatePolicy.Pinned);
        await Assert.ThrowsAsync<ArgumentException>(() => source.ReadAsync(nonCanonicalSource));
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly int _length;

        internal UnknownLengthContent(int length) => _length = length;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[_length]).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubRemoteCapabilityArtifactSource : IRemoteCapabilityArtifactSource
{
    internal Func<CapabilityArtifactSourceReference, CancellationToken, Task<CapabilityArtifactContent>> Handler { get; set; } = (_, _) => Task.FromResult(new CapabilityArtifactContent(CapabilityArtifactTestData.Content));
    internal int Calls { get; private set; }

    public Task<CapabilityArtifactContent> ReadAsync(CapabilityArtifactSourceReference source, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Handler(source, cancellationToken);
    }
}

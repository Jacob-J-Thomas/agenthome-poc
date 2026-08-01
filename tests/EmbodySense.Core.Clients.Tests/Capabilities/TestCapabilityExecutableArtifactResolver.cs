using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

internal sealed class TestCapabilityExecutableArtifactResolver : ICapabilityExecutableArtifactResolver
{
    private readonly string? _artifactRoot;

    internal TestCapabilityExecutableArtifactResolver(string? artifactRoot = null) => _artifactRoot = artifactRoot;

    public Task<CapabilityExecutableArtifactResolution> ResolveAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (_artifactRoot is null)
        {
            return Task.FromResult(new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Unavailable, null, "No test activation root is configured."));
        }
        var executablePath = Path.Combine(_artifactRoot, invocation.Manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
        ICapabilityExecutableArtifactLease lease = new TestCapabilityExecutableArtifactLease(_artifactRoot, executablePath, invocation.Manifest.Checksum, invocation.ExpectedActivationRevision);
        return Task.FromResult(new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Available, lease, "Test proved artifact."));
    }
}

using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityLifecycleTargetResolver : ICapabilityLifecycleTargetResolver
{
    internal CapabilityLifecycleTargetResolution Resolution { get; set; } = null!;
    internal CapabilityLifecycleTargetResolutionRequest? Request { get; private set; }
    internal int Calls { get; private set; }

    public Task<CapabilityLifecycleTargetResolution> ResolveAsync(CapabilityLifecycleTargetResolutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        Request = request;
        return Task.FromResult(Resolution);
    }
}

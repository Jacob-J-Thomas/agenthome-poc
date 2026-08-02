using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.IntegrationTests.Core.Capabilities;

internal sealed class NullCapabilityLifecycleBaselineSource : ICapabilityLifecycleBaselineSource
{
    public Task<CapabilityLifecycleBaseline?> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<CapabilityLifecycleBaseline?>(null);
    }
}

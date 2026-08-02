using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityLifecycleBaselineSource : ICapabilityLifecycleBaselineSource
{
    internal CapabilityLifecycleBaseline? Baseline { get; set; }
    internal CapabilityId? LastCapabilityId { get; private set; }

    public Task<CapabilityLifecycleBaseline?> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default)
    {
        LastCapabilityId = capabilityId;
        return Task.FromResult(Baseline);
    }
}

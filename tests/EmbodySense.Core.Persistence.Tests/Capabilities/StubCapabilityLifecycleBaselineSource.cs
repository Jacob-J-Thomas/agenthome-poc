using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubCapabilityLifecycleBaselineSource : ICapabilityLifecycleBaselineSource
{
    internal CapabilityLifecycleBaseline? Baseline { get; set; } = CapabilityLifecycleTestData.Baseline();
    internal int Reads { get; private set; }

    public Task<CapabilityLifecycleBaseline?> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        return Task.FromResult(Baseline);
    }
}

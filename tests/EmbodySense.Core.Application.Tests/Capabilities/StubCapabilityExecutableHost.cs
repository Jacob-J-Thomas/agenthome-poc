using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityExecutableHost : ICapabilityExecutableHost
{
    internal CapabilityExecutableAvailability Availability { get; set; } = new(CapabilityExecutableAvailabilityStatus.Available, "Available.");

    public CapabilityExecutableAvailability CheckAvailability(CapabilityArtifactManifest manifest) => Availability;

    public Task<CapabilityExecutableInvocationResult> InvokeAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

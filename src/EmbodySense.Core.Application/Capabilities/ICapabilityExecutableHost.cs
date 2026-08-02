using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Runs executable artifacts only through an explicit out-of-process isolation boundary.</summary>
public interface ICapabilityExecutableHost
{
    /// <summary>Checks whether every declared platform, resource, data, network, and secret boundary can be enforced.</summary>
    CapabilityExecutableAvailability CheckAvailability(CapabilityArtifactManifest manifest);

    /// <summary>Invokes one activated artifact with bounded standard streams, time, cancellation, and concurrency.</summary>
    Task<CapabilityExecutableInvocationResult> InvokeAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken = default);
}

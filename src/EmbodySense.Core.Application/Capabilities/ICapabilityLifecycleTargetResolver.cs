using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Resolves browser-selected lifecycle targets from bounded server-owned immutable artifact evidence.</summary>
public interface ICapabilityLifecycleTargetResolver
{
    /// <summary>Resolves one optional-version target without accepting client-supplied descriptors or digests.</summary>
    Task<CapabilityLifecycleTargetResolution> ResolveAsync(CapabilityLifecycleTargetResolutionRequest request, CancellationToken cancellationToken = default);
}

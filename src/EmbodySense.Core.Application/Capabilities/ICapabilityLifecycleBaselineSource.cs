using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Reads proved current catalog and activation state for first lifecycle registration only.</summary>
public interface ICapabilityLifecycleBaselineSource
{
    /// <summary>Reads the exact current baseline, or <see langword="null"/> when the capability is unknown or unproved.</summary>
    Task<CapabilityLifecycleBaseline?> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default);
}

using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Captures every registered dependent through one deterministic fail-closed contract.</summary>
public interface ICapabilityDependentIndex
{
    /// <summary>Reads and validates the complete current dependent set.</summary>
    Task<CapabilityDependentIndexSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}

namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Declares host-contract and platform compatibility without resolving availability.
/// </summary>
/// <param name="HostVersionRange">The compatible EmbodySense host contract range.</param>
/// <param name="SupportedPlatforms">The bounded set of supported platform tuples.</param>
public sealed record CapabilityCompatibility(CapabilityVersionRange HostVersionRange, IReadOnlyList<CapabilityPlatform> SupportedPlatforms)
{
    /// <summary>Gets a defensive read-only snapshot of the supported platform tuples.</summary>
    public IReadOnlyList<CapabilityPlatform> SupportedPlatforms { get; } = SupportedPlatforms is null ? null! : Array.AsReadOnly(SupportedPlatforms.ToArray());
}

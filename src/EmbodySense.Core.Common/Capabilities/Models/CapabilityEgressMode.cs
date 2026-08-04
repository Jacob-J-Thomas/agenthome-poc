namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Declares a capability's network-egress requirement without granting network authority.
/// </summary>
public enum CapabilityEgressMode
{
    /// <summary>The egress requirement is absent or unsupported.</summary>
    Unknown = 0,

    /// <summary>The capability does not require network egress.</summary>
    None = 1,

    /// <summary>The capability requires egress only to the declared destinations.</summary>
    Restricted = 2,

    /// <summary>The capability cannot identify a finite destination set.</summary>
    Unrestricted = 3
}

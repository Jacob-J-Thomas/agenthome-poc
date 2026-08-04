namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Identifies installation separately from declaration and enablement.</summary>
public enum CapabilityInstallationState
{
    /// <summary>The installation state is unknown.</summary>
    Unknown = 0,

    /// <summary>The implementation is not installed.</summary>
    NotInstalled = 1,

    /// <summary>The implementation is installed.</summary>
    Installed = 2
}

namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Identifies how an implementation entered the capability supply chain without asserting trust.
/// </summary>
public enum CapabilityProvenanceKind
{
    /// <summary>The provenance kind is absent or unsupported.</summary>
    Unknown = 0,

    /// <summary>The implementation ships as part of the EmbodySense runtime.</summary>
    BuiltIn = 1,

    /// <summary>The implementation comes from a user-owned local source.</summary>
    LocalSource = 2,

    /// <summary>The implementation comes from a package artifact.</summary>
    Package = 3,

    /// <summary>The implementation comes from a remote content-addressed artifact.</summary>
    RemoteArtifact = 4
}

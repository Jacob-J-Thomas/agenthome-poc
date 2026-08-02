namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies the bounded transport used to obtain a capability artifact.</summary>
public enum CapabilityArtifactSourceKind
{
    /// <summary>The source kind is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The artifact is read from a configured local source root.</summary>
    Local = 1,
    /// <summary>The artifact is read from a canonical HTTPS location.</summary>
    Remote = 2
}

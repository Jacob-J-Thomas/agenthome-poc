namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Classifies the maximum side-effect posture declared by a capability.
/// </summary>
public enum CapabilitySideEffectClass
{
    /// <summary>The side-effect class is absent or unsupported.</summary>
    Unknown = 0,

    /// <summary>The capability has no side effects.</summary>
    None = 1,

    /// <summary>The capability reads state without mutating it.</summary>
    ReadOnly = 2,

    /// <summary>The capability may make locally reversible changes.</summary>
    LocalReversible = 3,

    /// <summary>The capability may make externally visible but reversible changes.</summary>
    ExternalReversible = 4,

    /// <summary>The capability may make irreversible changes.</summary>
    Irreversible = 5
}

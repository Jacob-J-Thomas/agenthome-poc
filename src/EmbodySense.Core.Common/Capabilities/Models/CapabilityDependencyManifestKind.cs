namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Identifies the bounded artifact category declaring capability dependencies.</summary>
public enum CapabilityDependencyManifestKind
{
    /// <summary>The manifest kind is absent or unsupported.</summary>
    Unknown = 0,

    /// <summary>The manifest belongs to a local or packaged skill.</summary>
    Skill = 1,

    /// <summary>The manifest belongs to a loop package, not a live loop admission.</summary>
    LoopPackage = 2
}

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies a domain that can depend on a capability without transferring authority to lifecycle infrastructure.</summary>
public enum CapabilityDependentKind
{
    /// <summary>A persisted loop definition.</summary>
    Loop = 1,
    /// <summary>A future role definition supplied through an explicit registration seam.</summary>
    Role = 2,
    /// <summary>A future schedule definition supplied through an explicit registration seam.</summary>
    Schedule = 3,
    /// <summary>A local skill dependency manifest.</summary>
    Skill = 4,
    /// <summary>An activated immutable capability package.</summary>
    Package = 5
}

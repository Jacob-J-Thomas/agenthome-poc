namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Identifies the domain category of a capability without defining its domain-specific behavior.
/// </summary>
public enum CapabilityKind
{
    /// <summary>The value is absent or unsupported.</summary>
    Unknown = 0,

    /// <summary>The capability observes or creates a loop trigger.</summary>
    TriggerAdapter = 1,

    /// <summary>The capability supplies a graph node kind.</summary>
    GraphNode = 2,

    /// <summary>The capability can actuate a side effect.</summary>
    Actuator = 3,

    /// <summary>The capability supplies governed context.</summary>
    ContextSource = 4,

    /// <summary>The capability supplies a model profile.</summary>
    ModelProfile = 5,

    /// <summary>The capability supplies observations.</summary>
    ObservationSource = 6,

    /// <summary>The capability evaluates runtime behavior or output.</summary>
    Evaluation = 7,

    /// <summary>The capability supplies a reusable skill.</summary>
    Skill = 8,

    /// <summary>The capability supplies a governed hook.</summary>
    Hook = 9,

    /// <summary>The capability adapts an external interaction surface.</summary>
    SurfaceAdapter = 10
}

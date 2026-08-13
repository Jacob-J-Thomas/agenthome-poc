namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Classifies one deterministic sequential plan-build decision.</summary>
public enum GovernedLoopSequentialPlanBuildStatus
{
    /// <summary>No supported decision was produced.</summary>
    Unknown = 0,
    /// <summary>The exact supported linear plan was produced.</summary>
    Ready,
    /// <summary>The immutable graph artifact is absent or invalid.</summary>
    InvalidArtifact,
    /// <summary>At least one exact kind, type identifier, and version is unsupported.</summary>
    UnsupportedDescriptor,
    /// <summary>The graph is not a supported entry-trigger, one-to-five inference, successful-exit topology.</summary>
    UnsupportedTopology,
    /// <summary>The graph does not match the exact first-wave node, port, parameter, authority, binding, schema, and output contract.</summary>
    UnsupportedContract,
}

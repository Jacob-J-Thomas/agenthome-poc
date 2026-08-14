namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Identifies the closed outcome of projecting one exact canonical sequential graph into the fenced ordered-runtime definition.</summary>
public enum GovernedLoopSequentialLegacyDefinitionProjectionStatus
{
    /// <summary>No supported projection outcome was produced.</summary>
    Unknown = 0,

    /// <summary>The exact canonical hand-off produced one valid deterministic legacy definition.</summary>
    Ready = 1,

    /// <summary>The immutable invocation snapshot or adapter binding was invalid or mismatched.</summary>
    InvalidBinding = 2,

    /// <summary>The graph artifact was invalid or did not match the adapter binding.</summary>
    InvalidArtifact = 3,

    /// <summary>The supplied plan was not the exact deterministic plan rebuilt from the artifact.</summary>
    InvalidPlan = 4,

    /// <summary>The exact canonical inputs could not form a valid fenced legacy definition.</summary>
    InvalidProjection = 5,
}

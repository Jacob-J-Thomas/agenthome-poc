namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Classifies whether exact node inputs were resolved, conclusively rejected, or unavailable for retry.</summary>
public enum GovernedLoopSequentialBindingResolutionStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,

    /// <summary>Every exact input was resolved against canonical retained evidence.</summary>
    Resolved = 1,

    /// <summary>One required binding or its source evidence was invalid, divergent, or unsupported.</summary>
    Invalid = 2,

    /// <summary>A required canonical source could not be read safely and must be retried without durable advancement.</summary>
    Unavailable = 3,
}

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Describes one wake-less approval publication outcome without implying continuation release.</summary>
public enum HumanReviewPublicationRecoveryItemStatus
{
    /// <summary>No supported item outcome was produced.</summary>
    Unknown = 0,

    /// <summary>The canonical continuation wake was published.</summary>
    Published = 1,

    /// <summary>The exact canonical continuation wake was already present.</summary>
    Replayed = 2,

    /// <summary>The reservation changed or publication remains response-unknown and is retained for a later pass.</summary>
    Parked = 3,

    /// <summary>The retained reservation or canonical publication result was invalid.</summary>
    Invalid = 4,
}

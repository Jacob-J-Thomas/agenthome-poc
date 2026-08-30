namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>Identifies the durable outcome disposition of one canonical sequential node attempt.</summary>
public enum CustomLoopSequentialNodeDisposition
{
    /// <summary>No terminal disposition has been observed.</summary>
    Unknown = 0,

    /// <summary>The node completed and its exact outcome evidence is durable.</summary>
    Completed,

    /// <summary>The node was definitively rejected without an ambiguous external effect.</summary>
    Rejected,

    /// <summary>The node outcome is ambiguous and requires durable human attention.</summary>
    NeedsReview,

    /// <summary>The exact activation is deliberately parked for a requested Human Review without ambiguous effect evidence.</summary>
    ReviewPending,
}

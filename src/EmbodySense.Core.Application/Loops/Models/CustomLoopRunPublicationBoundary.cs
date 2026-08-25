namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>Identifies an observable boundary in canonical custom-loop run publication.</summary>
public enum CustomLoopRunPublicationBoundary
{
    /// <summary>The sibling staging file was fully flushed before publication.</summary>
    StagedFileFlushed = 1,

    /// <summary>The exact retained-parent rename completed and the durable outcome is not yet known.</summary>
    CanonicalRenamed = 2,

    /// <summary>The retained parent-directory durability barrier completed.</summary>
    ParentDirectoryFlushed = 3,

    /// <summary>The reopened canonical target was proven to retain the staged identity and exact bytes.</summary>
    TargetProven = 4,
}

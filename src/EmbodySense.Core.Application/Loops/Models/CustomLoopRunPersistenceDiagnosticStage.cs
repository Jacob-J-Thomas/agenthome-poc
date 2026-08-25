namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>Identifies the bounded run-store persistence stage that failed.</summary>
public enum CustomLoopRunPersistenceDiagnosticStage
{
    /// <summary>No specific persistence stage was retained.</summary>
    Unknown = 0,

    /// <summary>A canonical run artifact could not be read safely.</summary>
    Read = 1,

    /// <summary>Canonical run content or a lifecycle transition could not be validated.</summary>
    Validate = 2,

    /// <summary>A staged canonical run artifact could not be atomically replaced.</summary>
    CanonicalReplace = 3,

    /// <summary>The derived discovery index could not be read, validated, or updated.</summary>
    Index = 4,

    /// <summary>The discovery-index pending marker could not be retained.</summary>
    Pending = 5,

    /// <summary>The retained parent-directory durability barrier or target proof could not be completed.</summary>
    CanonicalDirectoryBarrier = 6,
}

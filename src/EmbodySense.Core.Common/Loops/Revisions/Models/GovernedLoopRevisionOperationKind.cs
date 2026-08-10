namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Identifies one closed governed-loop revision lifecycle operation.</summary>
public enum GovernedLoopRevisionOperationKind
{
    /// <summary>No supported operation was supplied.</summary>
    Unknown = 0,
    /// <summary>Create the graph's first immutable draft.</summary>
    CreateDraft,
    /// <summary>Create a new immutable successor from the exact current draft, or from the current publication when no draft exists.</summary>
    ReplaceDraft,
    /// <summary>Publish an exact validated draft.</summary>
    Publish,
    /// <summary>Disable the exact published revision without changing its bytes.</summary>
    Disable,
    /// <summary>Archive the graph lifecycle without deleting immutable history.</summary>
    Archive,
    /// <summary>Create and publish a new immutable successor citing a selected historical revision.</summary>
    Rollback
}

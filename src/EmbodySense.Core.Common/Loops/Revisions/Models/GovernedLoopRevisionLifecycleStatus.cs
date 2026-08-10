namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Identifies the closed lifecycle posture of one governed-loop graph.</summary>
public enum GovernedLoopRevisionLifecycleStatus
{
    /// <summary>No supported lifecycle posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The graph has an immutable draft and no active published revision.</summary>
    Draft,
    /// <summary>The graph has one exact active published revision.</summary>
    Published,
    /// <summary>The exact published revision is disabled but remains inspectable.</summary>
    Disabled,
    /// <summary>The graph lifecycle is terminal and its immutable history remains inspectable.</summary>
    Archived
}

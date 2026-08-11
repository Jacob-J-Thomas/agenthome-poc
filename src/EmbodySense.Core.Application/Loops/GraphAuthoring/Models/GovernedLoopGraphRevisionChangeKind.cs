namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Classifies the bounded semantic delta produced by graph authoring.</summary>
public enum GovernedLoopGraphRevisionChangeKind
{
    /// <summary>No trustworthy classification is available.</summary>
    Unknown = 0,
    /// <summary>The graph received its first immutable revision.</summary>
    Initial = 1,
    /// <summary>Executable content changed.</summary>
    Executable = 2,
    /// <summary>Only display or layout content changed.</summary>
    LayoutOnly = 3,
    /// <summary>A retained historical publication was copied into a new successor.</summary>
    RollbackCopy = 4,
    /// <summary>Only lifecycle posture changed; no immutable graph payload was written.</summary>
    LifecycleOnly = 5,
    /// <summary>A new revision identity was authored with identical executable and layout content.</summary>
    IdentityOnly = 6,
}

namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Identifies one deterministic selection outcome.</summary>
public enum TriggerWorkerSelectionStatus
{
    /// <summary>Ownership was committed.</summary>
    Acquired,

    /// <summary>No eligible entry exists.</summary>
    Empty,

    /// <summary>The expected queue generation is stale.</summary>
    RevisionConflict,

    /// <summary>The supplied clock moved behind persisted worker evidence.</summary>
    ClockRollback,

    /// <summary>Persistence could not safely commit the selection.</summary>
    Unavailable
}

namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Identifies one closed orchestration outcome without selecting recovery policy.</summary>
public enum GovernedLoopEffectAttemptExecutionStatus
{
    /// <summary>No supported outcome was selected.</summary>
    Unknown = 0,

    /// <summary>A conclusive effect outcome was committed.</summary>
    Committed = 1,

    /// <summary>Exact retained evidence was replayed without dispatch.</summary>
    Replayed = 2,

    /// <summary>Dispatch was affirmatively proved not to have started.</summary>
    DispatchNotStarted = 3,

    /// <summary>The irreversible boundary may have been crossed without conclusive outcome evidence.</summary>
    ReconciliationRequired = 4,

    /// <summary>The request, structured input, or operation contract was invalid.</summary>
    InvalidRequest = 5,

    /// <summary>The exact capability lifecycle or server registration was not currently available.</summary>
    CatalogUnavailable = 6,

    /// <summary>Current authority stopped the effect before dispatch.</summary>
    AuthorityStopped = 7,

    /// <summary>The stable operation generation was reused with different authorized content or stale evidence.</summary>
    Conflict = 8,

    /// <summary>Another executor currently owns the exact attempt generation.</summary>
    OperationInProgress = 9,

    /// <summary>Durable attempt capacity was exhausted.</summary>
    Backpressured = 10,

    /// <summary>Required durable evidence was corrupt or unavailable.</summary>
    EvidenceUnavailable = 11,

    /// <summary>A separate governed approval proof is required before this operation may dispatch.</summary>
    ApprovalRequired = 12,
}

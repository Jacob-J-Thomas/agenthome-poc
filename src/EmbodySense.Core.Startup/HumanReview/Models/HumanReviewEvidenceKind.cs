namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies an append-only projected Human Review evidence event.</summary>
public enum HumanReviewEvidenceKind
{
    /// <summary>No supported evidence kind was supplied.</summary>
    Unknown = 0,
    /// <summary>The immutable request and exact parked frontier were atomically admitted.</summary>
    RequestAdmitted = 1,
    /// <summary>The durable request became available to projection surfaces.</summary>
    RequestPublished = 2,
    /// <summary>A bounded reminder was recorded without changing the parked frontier.</summary>
    ReminderRecorded = 3,
    /// <summary>A bounded escalation was recorded without changing the parked frontier.</summary>
    EscalationRecorded = 4,
    /// <summary>A decision operation was durably attempted.</summary>
    DecisionAttempted = 5,
    /// <summary>A terminal decision was accepted.</summary>
    DecisionAccepted = 6,
    /// <summary>A request-for-information decision was accepted while retaining the frontier.</summary>
    InformationRequested = 7,
    /// <summary>A submitted decision conflicted with exact durable state.</summary>
    DecisionConflict = 8,
    /// <summary>The request conflicted with exact durable state.</summary>
    RequestConflict = 9,
    /// <summary>The pending request became expired.</summary>
    RequestExpired = 10,
    /// <summary>The request became superseded by exact durable state drift.</summary>
    RequestSuperseded = 11,
    /// <summary>The exact approved continuation was reserved once.</summary>
    ContinuationReserved = 12,
    /// <summary>The reserved continuation completed once.</summary>
    ContinuationCompleted = 13,
    /// <summary>Fresh revalidation blocked release before the irreversible boundary.</summary>
    PreDispatchBlocked = 14,
    /// <summary>A decision operation was denied without accepting a decision.</summary>
    DecisionDenied = 15,
    /// <summary>A decision operation arrived after expiry.</summary>
    DecisionExpired = 16
}

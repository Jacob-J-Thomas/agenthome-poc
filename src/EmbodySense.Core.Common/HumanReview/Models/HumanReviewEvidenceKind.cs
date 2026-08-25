namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one append-only Human Review evidence event.</summary>
public enum HumanReviewEvidenceKind
{
    /// <summary>No supported evidence kind was supplied.</summary>
    Unknown = 0,
    /// <summary>The immutable request and exact parked frontier were atomically admitted.</summary>
    RequestAdmitted = 1,
    /// <summary>The durable request became available to one or more projection surfaces after admission.</summary>
    RequestPublished = 2,
    /// <summary>A durable reminder became due or was published without changing the parked frontier.</summary>
    ReminderRecorded = 3,
    /// <summary>A bounded durable escalation became due or was published without changing the parked frontier.</summary>
    EscalationRecorded = 4,
    /// <summary>An authenticated reviewer decision operation was durably attempted before its final disposition.</summary>
    DecisionAttempted = 5,
    /// <summary>A terminal approval, rejection, or cancellation decision was accepted.</summary>
    DecisionAccepted = 6,
    /// <summary>A request-for-information decision was accepted while retaining the frontier.</summary>
    InformationRequested = 7,
    /// <summary>A submitted decision conflicted with current exact request, lifecycle, or operation state and was not accepted.</summary>
    DecisionConflict = 8,
    /// <summary>The request itself conflicted with current exact durable state and cannot be released.</summary>
    RequestConflict = 9,
    /// <summary>The pending request became expired.</summary>
    RequestExpired = 10,
    /// <summary>The request became superseded by exact durable state drift or replacement.</summary>
    RequestSuperseded = 11,
    /// <summary>The exact approved continuation was reserved once.</summary>
    ContinuationReserved = 12,
    /// <summary>The reserved continuation completed once.</summary>
    ContinuationCompleted = 13,
    /// <summary>Fresh revalidation blocked release before the irreversible boundary while retaining the exact approved decision reference.</summary>
    PreDispatchBlocked = 14
    ,
    /// <summary>A decision operation was denied without accepting a decision.</summary>
    DecisionDenied = 15,
    /// <summary>A decision operation arrived after expiry without overloading request-expiry evidence.</summary>
    DecisionExpired = 16
}

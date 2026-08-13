namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Defines closed outcomes for one bounded due-occurrence evaluation.</summary>
public enum ScheduleEvaluationStatus
{
    /// <summary>No recognized outcome was produced.</summary>
    Unknown = 0,
    /// <summary>The requested schedule does not exist.</summary>
    NotFound = 1,
    /// <summary>The next occurrence is not due.</summary>
    NotDue = 2,
    /// <summary>The schedule is disabled and has no pending recovery work.</summary>
    Disabled = 3,
    /// <summary>The recurrence is exhausted.</summary>
    Exhausted = 4,
    /// <summary>One missed occurrence was durably skipped.</summary>
    Skipped = 5,
    /// <summary>One overlapping occurrence remains durably deferred.</summary>
    Deferred = 6,
    /// <summary>One exact occurrence was newly queued and finalized.</summary>
    Queued = 7,
    /// <summary>An exact durable queue outcome was replayed and finalized.</summary>
    Replayed = 8,
    /// <summary>Queue admission conclusively rejected and finalized the occurrence.</summary>
    Rejected = 9,
    /// <summary>A bounded dependency refused the operation under load.</summary>
    Backpressured = 10,
    /// <summary>Current authority or recurrence permission denied preparation.</summary>
    PermissionDenied = 11,
    /// <summary>An optimistic state transition lost to another evaluator.</summary>
    Conflict = 12,
    /// <summary>A dependency could not safely complete or report the operation.</summary>
    Unavailable = 13,
    /// <summary>Persisted or returned evidence failed integrity validation.</summary>
    Corrupt = 14,
    /// <summary>The durable wall-clock watermark is later than the current observation.</summary>
    ClockRollback = 15,
    /// <summary>The queue outcome is ambiguous and requires operator reconciliation.</summary>
    NeedsReview = 16,
    /// <summary>A schema-1 evidence or recurrence bound prevented safe progress.</summary>
    BoundExceeded = 17,
}

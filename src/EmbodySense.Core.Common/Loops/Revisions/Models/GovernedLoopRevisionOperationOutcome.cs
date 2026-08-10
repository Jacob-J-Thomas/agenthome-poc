namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Identifies the closed durable outcome of one revision lifecycle operation.</summary>
public enum GovernedLoopRevisionOperationOutcome
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The requested immutable evidence and head were committed.</summary>
    Committed,
    /// <summary>The request conflicted with retained operation intent or optimistic state.</summary>
    Conflict,
    /// <summary>An exact lifecycle, revision, or publication target was not found.</summary>
    NotFound,
    /// <summary>The operation could not commit without exceeding a finite retention bound.</summary>
    LimitExceeded,
    /// <summary>The caller cannot yet prove whether the durable operation committed.</summary>
    OutcomeUnknown
}

namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Identifies the outcome of scheduling one exact next retry attempt.</summary>
public enum GovernedLoopRetryExecutionStatus
{
    /// <summary>The request was invalid or conflicted with retained evidence.</summary>
    Conflict = 1,
    /// <summary>The exact retry was durably scheduled.</summary>
    Scheduled,
    /// <summary>The exact schedule already existed and was replayed.</summary>
    Replayed,
    /// <summary>Policy or current posture did not admit retry.</summary>
    Ineligible,
    /// <summary>A finite attempt, elapsed-time, or resource bound was exhausted.</summary>
    Exhausted,
    /// <summary>Unknown, ambiguous, or corrupt evidence requires review.</summary>
    NeedsReview,
    /// <summary>A required current adapter or store was unavailable.</summary>
    Unavailable,
}

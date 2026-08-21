namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Identifies the closed result of evaluating one exact failed attempt against its retry contract.</summary>
public enum GovernedLoopRetryDecisionStatus
{
    /// <summary>No supported decision was produced.</summary>
    Unknown = 0,
    /// <summary>The exact next attempt may be durably scheduled.</summary>
    Schedule,
    /// <summary>The exact next attempt is immediately due but still requires durable checkpoint publication.</summary>
    Due,
    /// <summary>The authored attempt, elapsed-time, or resource budget is exhausted.</summary>
    Exhausted,
    /// <summary>The exact classified failure is not eligible for automatic retry.</summary>
    NoRetry,
    /// <summary>An authenticated cancellation outranks automatic retry.</summary>
    Cancelled,
    /// <summary>An authenticated pause outranks automatic retry.</summary>
    Paused,
    /// <summary>Retained evidence conflicts with the exact evaluation coordinates.</summary>
    Conflict,
    /// <summary>The failure or current posture is not eligible for automatic retry.</summary>
    Ineligible,
    /// <summary>Required authoritative usage evidence is unavailable, so a hard ceiling cannot be proven.</summary>
    NeedsReview,
    /// <summary>The request or retained retry evidence is malformed or substituted.</summary>
    Invalid,
}

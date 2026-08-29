namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

/// <summary>Classifies one bounded attempt to discover, persist, and submit an exact Human Input response wake.</summary>
public enum HumanInputResponseContinuationWakeStatus
{
    /// <summary>The response wake was durably submitted to the canonical generic wake plane.</summary>
    Submitted = 1,

    /// <summary>The same response wake was already durably submitted or completed.</summary>
    Replayed = 2,

    /// <summary>The candidate is no longer an exact pending Human Input continuation.</summary>
    Stale = 3,

    /// <summary>The candidate or authoritative response evidence was malformed or divergent.</summary>
    Invalid = 4,

    /// <summary>Authoritative state could not establish a safe response wake.</summary>
    Unavailable = 5,

    /// <summary>An accepted response was not present, and an exact expired, cancelled, rejected, or unresolved-supersession lifecycle outcome was atomically converged without a wake.</summary>
    Retired = 6,

    /// <summary>The exact request remains pending with no accepted selection and no authoritative terminal lifecycle operation.</summary>
    NoWork = 7,
}

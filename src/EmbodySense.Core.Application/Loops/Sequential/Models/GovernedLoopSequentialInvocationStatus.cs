namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Identifies the closed outcome of canonical sequential admission coordination.</summary>
public enum GovernedLoopSequentialInvocationStatus
{
    /// <summary>No supported result was produced.</summary>
    Unknown = 0,

    /// <summary>The exact admitted run was offered to the fenced ordered runtime.</summary>
    Executed = 1,

    /// <summary>The exact durable run was already terminal; a Completed run may have reconciled grant-completion evidence without repeating graph execution.</summary>
    Terminal = 2,

    /// <summary>Canonical admission committed or replayed a definitive rejection.</summary>
    Rejected = 3,

    /// <summary>The request or retained evidence is malformed or does not compose.</summary>
    Invalid = 4,

    /// <summary>An operation identity or immutable coordinate is bound to different evidence.</summary>
    Conflict = 5,

    /// <summary>The required pre-admission invocation receipt does not exist.</summary>
    NotFound = 6,

    /// <summary>A required durable dependency could not be read or written safely.</summary>
    Unavailable = 7,

    /// <summary>A bounded admission, run, or receipt limit rejected the operation.</summary>
    LimitExceeded = 8,

    /// <summary>Admission or invocation audit completion cannot be proved durable.</summary>
    AuditUnavailable = 9,

    /// <summary>A nonterminal run exists but requires recovery or an explicit lifecycle transition instead of first dispatch.</summary>
    RecoveryRequired = 10,

    /// <summary>The authenticated Skip policy durably closed the occurrence before provider dispatch.</summary>
    OverlapSkipped = 11,

    /// <summary>The authenticated DeferOne policy durably retained the occurrence for later reselection.</summary>
    OverlapDeferred = 12,

    /// <summary>The authenticated Allow policy durably retained the occurrence for serialized reselection.</summary>
    OverlapSerialized = 13,

    /// <summary>An existing exact DeferOne occurrence retained the single deferred slot.</summary>
    DeferredOneSuppressed = 14,

    /// <summary>The exact run and admission receipt are durable, but provider execution was intentionally not entered.</summary>
    Prepared = 15,

    /// <summary>The exact scheduled occurrence was terminalized before its detailed admission evidence was compacted.</summary>
    Retired = 16,
}

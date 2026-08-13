namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Identifies the closed result of materializing one admitted canonical run into the fenced ordered store.</summary>
public enum GovernedLoopSequentialMaterializationStatus
{
    /// <summary>No supported result was produced.</summary>
    Unknown = 0,

    /// <summary>A new exact run and its admission-audit completion marker are durable.</summary>
    Ready = 1,

    /// <summary>An exact existing run was authenticated and, when needed, its admission audit was reconciled.</summary>
    Replayed = 2,

    /// <summary>The supplied canonical contracts were invalid or did not compose.</summary>
    Invalid = 3,

    /// <summary>Existing durable evidence is bound to different canonical coordinates.</summary>
    Conflict = 4,

    /// <summary>Required durable evidence could not be read or committed safely.</summary>
    Unavailable = 5,

    /// <summary>The ordered store rejected creation because its bounded trace limit was reached.</summary>
    LimitExceeded = 6,

    /// <summary>A different nonterminal run already owns the projected loop identity.</summary>
    NonterminalRunExists = 7,

    /// <summary>The run may be durable, but admission-audit completion cannot yet be proved.</summary>
    AuditUnavailable = 8,

    /// <summary>The stable admission-audit operation is durably bound to different evidence.</summary>
    AuditConflict = 9,

    /// <summary>The authenticated Skip policy durably terminalized the occurrence before provider dispatch.</summary>
    OverlapSkipped = 10,

    /// <summary>The authenticated DeferOne policy durably retained the occurrence for later reselection.</summary>
    OverlapDeferred = 11,

    /// <summary>The authenticated Allow policy durably retained the occurrence while preserving serial execution.</summary>
    OverlapSerialized = 12,

    /// <summary>Another exact DeferOne occurrence already owns the single deferred slot.</summary>
    DeferredOneSuppressed = 13,
}

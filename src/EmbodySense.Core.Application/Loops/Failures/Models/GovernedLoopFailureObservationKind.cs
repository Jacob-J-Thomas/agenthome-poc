namespace EmbodySense.Core.Application.Loops.Failures.Models;

/// <summary>Identifies one bounded server-owned observation accepted by the schema-1 failure classifier.</summary>
public enum GovernedLoopFailureObservationKind
{
    /// <summary>An undefined observation.</summary>
    Unknown = 0,
    /// <summary>Validation or immutable configuration rejected the operation.</summary>
    ValidationRejected,
    /// <summary>Current authority denied the operation.</summary>
    AuthorityDenied,
    /// <summary>Previously admitted authority was revoked or narrowed.</summary>
    AuthorityRevoked,
    /// <summary>An authenticated reviewer rejected the operation.</summary>
    HumanReviewRejected,
    /// <summary>A dependency was unavailable before dispatch.</summary>
    DependencyUnavailable,
    /// <summary>Dispatch was proved not to have started.</summary>
    DispatchProvedNotStarted,
    /// <summary>No effect occurred and exact-intent retry is safe for later policy to consider.</summary>
    RetryableNoEffect,
    /// <summary>A conclusive terminal failure occurred.</summary>
    TerminalFailure,
    /// <summary>A target or optimistic precondition conflicted.</summary>
    TargetConflict,
    /// <summary>A timeout occurred with proof of no effect.</summary>
    TimeoutNoEffect,
    /// <summary>Cancellation occurred with proof of no effect.</summary>
    CancellationNoEffect,
    /// <summary>Output was malformed.</summary>
    MalformedOutput,
    /// <summary>Output violated a closed policy.</summary>
    PolicyInvalidOutput,
    /// <summary>A quota was exhausted.</summary>
    QuotaExhausted,
    /// <summary>An enclosing deadline was exhausted.</summary>
    DeadlineExhausted,
    /// <summary>An iteration bound was exhausted.</summary>
    IterationExhausted,
    /// <summary>A cost bound was exhausted.</summary>
    CostExhausted,
    /// <summary>An authenticated user paused the run.</summary>
    UserPaused,
    /// <summary>An authenticated user cancelled the run.</summary>
    UserCancelled,
    /// <summary>The schema is unsupported.</summary>
    UnsupportedSchema,
    /// <summary>The capability is unsupported.</summary>
    UnsupportedCapability,
    /// <summary>An external outcome may exist.</summary>
    AmbiguousOutcome,
    /// <summary>Persistence integrity is incomplete.</summary>
    PersistenceIntegrityFailure,
    /// <summary>Audit integrity is incomplete.</summary>
    AuditIntegrityFailure,
    /// <summary>Evidence is missing, malformed, or contradictory.</summary>
    EvidenceIntegrityFailure,
    /// <summary>An explicit admitted Fail terminal selected a bounded failure.</summary>
    AgentSelectedFailure,
}

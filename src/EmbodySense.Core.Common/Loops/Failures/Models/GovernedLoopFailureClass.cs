namespace EmbodySense.Core.Common.Loops.Failures.Models;

/// <summary>Classifies one proved schema-1 governed-loop failure without selecting recovery policy.</summary>
public enum GovernedLoopFailureClass
{
    /// <summary>An undefined failure class.</summary>
    Unknown = 0,
    /// <summary>Validation or immutable configuration was rejected.</summary>
    ValidationConfiguration,
    /// <summary>Current authority or permission denied the operation.</summary>
    AuthorityPermissionDenied,
    /// <summary>An authenticated human review rejected the operation.</summary>
    ReviewRejected,
    /// <summary>A required dependency was unavailable before dispatch.</summary>
    DependencyUnavailableBeforeDispatch,
    /// <summary>Evidence proves dispatch did not begin.</summary>
    DispatchProvedNotStarted,
    /// <summary>Evidence proves a retryable failure with no external effect.</summary>
    RetryableNoEffect,
    /// <summary>A conclusive non-retryable terminal failure occurred.</summary>
    TerminalFailure,
    /// <summary>A target or optimistic precondition conflicted.</summary>
    TargetPreconditionConflict,
    /// <summary>Timeout or cancellation was observed with proof that no effect occurred.</summary>
    TimeoutCancellationNoEffect,
    /// <summary>An output was malformed or policy-invalid.</summary>
    MalformedPolicyInvalidOutput,
    /// <summary>An enclosing quota, deadline, iteration, or cost bound was exhausted.</summary>
    Exhaustion,
    /// <summary>An authenticated user paused the run.</summary>
    UserPaused,
    /// <summary>An authenticated user cancelled the run.</summary>
    UserCancelled,
    /// <summary>A schema or capability is unsupported.</summary>
    UnsupportedSchemaCapability,
    /// <summary>An external outcome may exist and is not safely classifiable.</summary>
    AmbiguousExternalOutcome,
    /// <summary>Persistence, audit, or evidence integrity is incomplete or contradictory.</summary>
    EvidenceIntegrityFailure,
    /// <summary>An explicit admitted Fail terminal selected a bounded failure.</summary>
    AgentSelectedFailure,
}

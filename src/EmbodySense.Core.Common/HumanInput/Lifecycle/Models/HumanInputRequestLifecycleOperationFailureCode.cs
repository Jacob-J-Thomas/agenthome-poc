namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

/// <summary>Identifies one bounded value-free Human Input request lifecycle failure.</summary>
public enum HumanInputRequestLifecycleOperationFailureCode
{
    /// <summary>No supported failure was supplied.</summary>
    Unknown = 0,
    /// <summary>No failure applies to a committed operation.</summary>
    None = 1,
    /// <summary>The expected lifecycle head or exact request version was stale.</summary>
    OptimisticStateConflict = 2,
    /// <summary>The operation identifier was already bound to changed canonical intent.</summary>
    OperationIntentConflict = 3,
    /// <summary>The exact target request lifecycle did not exist.</summary>
    LifecycleNotFound = 4,
    /// <summary>Create targeted a request lifecycle that already existed.</summary>
    LifecycleAlreadyExists = 5,
    /// <summary>The requested transition targeted a terminal lifecycle.</summary>
    LifecycleTerminal = 6,
    /// <summary>The candidate request changed fields outside the requested operation or collided with retained state.</summary>
    CandidateRequestConflict = 7,
    /// <summary>The trusted lifecycle timing boundary was not satisfied.</summary>
    TimingBoundaryConflict = 8,
    /// <summary>The immutable per-request or workspace request-version bound was exhausted.</summary>
    RequestVersionLimitExceeded = 9,
    /// <summary>The finite reminder bound was exhausted.</summary>
    ReminderLimitExceeded = 10,
    /// <summary>The append-only operation-evidence bound was exhausted.</summary>
    OperationEvidenceLimitExceeded = 11,
    /// <summary>The finite request-head bound was exhausted.</summary>
    RequestLimitExceeded = 12,
    /// <summary>The interoperable optimistic lifecycle-version bound was exhausted.</summary>
    LifecycleVersionLimitExceeded = 13
}

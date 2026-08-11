namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

/// <summary>Classifies one atomic non-renewable authority-usage result.</summary>
public enum GovernedLoopEffectAuthorityUsageStoreStatus
{
    /// <summary>No target or completion mutation was required and no completion claim exists.</summary>
    Allowed = 1,

    /// <summary>A new distinct target was durably reserved.</summary>
    TargetReserved = 2,

    /// <summary>The same target was already reserved anywhere in the exact admitted run.</summary>
    TargetAlreadyReserved = 3,

    /// <summary>The first exact bound-run completion is durably pending immediately before its terminal callback.</summary>
    CompletionPending = 4,

    /// <summary>The same exact run and completion operation are already durably pending and may resume the idempotent terminal callback.</summary>
    CompletionAlreadyPending = 5,

    /// <summary>The terminal callback succeeded and the exact grant is now durably completed.</summary>
    CompletionCompleted = 6,

    /// <summary>The same exact run and completion operation are already durably completed.</summary>
    CompletionAlreadyCompleted = 7,

    /// <summary>The current distinct-target ceiling is already exhausted.</summary>
    TargetLimitExceeded = 8,

    /// <summary>The exact grant already has a durable first-bound-run completion claim.</summary>
    GrantCompleted = 9,

    /// <summary>The usage ledger could not be read or advanced before any mutation could have committed.</summary>
    Unavailable = 10,

    /// <summary>The usage ledger may have advanced, or retained evidence admits more than one safe interpretation.</summary>
    Ambiguous = 11,

    /// <summary>Existing exact-scope usage evidence conflicts with the supplied immutable coordinates.</summary>
    Conflict = 12
}

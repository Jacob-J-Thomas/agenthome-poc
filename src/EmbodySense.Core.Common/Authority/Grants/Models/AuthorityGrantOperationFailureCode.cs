namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Identifies bounded value-free failure evidence for one grant operation.</summary>
public enum AuthorityGrantOperationFailureCode
{
    /// <summary>No failure applies to a committed or replayed operation.</summary>
    None = 0,
    /// <summary>The request contract or canonical hash was invalid.</summary>
    InvalidRequest = 1,
    /// <summary>The actor was not authorized for the exact request.</summary>
    AuthorityDenied = 2,
    /// <summary>Server-owned authority evidence was unavailable or malformed.</summary>
    AuthorityUnavailable = 3,
    /// <summary>The expected grant lifecycle state conflicted.</summary>
    LifecycleConflict = 4,
    /// <summary>An operation identifier was already bound to changed intent.</summary>
    OperationConflict = 5,
    /// <summary>The exact profile dependency was not currently active and matching.</summary>
    ProfileUnavailable = 6,
    /// <summary>The exact role dependency was not currently active and matching.</summary>
    RoleUnavailable = 7,
    /// <summary>The exact loop publication or its role binding was not currently active and matching.</summary>
    LoopUnavailable = 8,
    /// <summary>The requested ceiling was not a subset of every exact dependency maximum.</summary>
    CeilingExceeded = 9,
    /// <summary>The trusted lifecycle boundary was not satisfied.</summary>
    BoundaryConflict = 10,
    /// <summary>A finite evidence or revision quota was exhausted.</summary>
    LimitExceeded = 11,
    /// <summary>Persistence was unavailable before durable intent.</summary>
    StoreUnavailable = 12,
    /// <summary>The post-intent durable outcome was ambiguous.</summary>
    StoreAmbiguous = 13,
}

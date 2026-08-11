namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Identifies one authority-grant mutation outcome.</summary>
public enum AuthorityGrantMutationStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>An immutable successor and operation evidence were committed.</summary>
    Committed = 1,
    /// <summary>Exact durable evidence for the request was replayed.</summary>
    Replayed = 2,
    /// <summary>The request contract was invalid.</summary>
    Invalid = 3,
    /// <summary>Current server-owned authority denied the request.</summary>
    Denied = 4,
    /// <summary>The target grant was absent.</summary>
    NotFound = 5,
    /// <summary>Optimistic state, changed intent, or exact dependency evidence conflicted.</summary>
    Conflict = 6,
    /// <summary>An exact profile, role, or loop dependency was unavailable or inactive.</summary>
    DependencyUnavailable = 7,
    /// <summary>The requested ceiling exceeded an exact dependency maximum.</summary>
    CeilingExceeded = 8,
    /// <summary>The trusted lifecycle boundary was not satisfied.</summary>
    BoundaryConflict = 9,
    /// <summary>A finite evidence or revision limit was exhausted.</summary>
    LimitExceeded = 10,
    /// <summary>Durable work could not begin safely.</summary>
    Unavailable = 11,
    /// <summary>A post-intent durable outcome could not be proved.</summary>
    Ambiguous = 12,
}

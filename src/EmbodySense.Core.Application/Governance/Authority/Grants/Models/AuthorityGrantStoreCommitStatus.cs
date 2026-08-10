namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Identifies one atomic authority-grant store disposition.</summary>
public enum AuthorityGrantStoreCommitStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact mutation was committed.</summary>
    Committed = 1,
    /// <summary>The exact operation was already durably committed.</summary>
    Replayed = 2,
    /// <summary>The optimistic store generation changed before commit.</summary>
    StoreConflict = 3,
    /// <summary>The operation identity is durably bound to changed intent.</summary>
    OperationConflict = 4,
    /// <summary>A finite durable quota is exhausted.</summary>
    LimitExceeded = 5,
    /// <summary>Durable intent did not begin.</summary>
    Unavailable = 6,
    /// <summary>The post-intent outcome cannot be proved.</summary>
    Ambiguous = 7,
}

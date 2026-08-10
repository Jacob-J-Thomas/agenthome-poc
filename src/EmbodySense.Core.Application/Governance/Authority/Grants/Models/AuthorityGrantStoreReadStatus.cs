namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Identifies whether a grant store read produced trustworthy state.</summary>
public enum AuthorityGrantStoreReadStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The current snapshot and global operation observation are trustworthy.</summary>
    Ready = 1,
    /// <summary>No grant exists at the exact identity.</summary>
    NotFound = 2,
    /// <summary>The workspace-global operation identity belongs to a different authority-record family.</summary>
    OperationConflict = 3,
    /// <summary>No trustworthy read could begin.</summary>
    Unavailable = 4,
    /// <summary>Durable evidence could not prove one consistent observation.</summary>
    Ambiguous = 5,
}

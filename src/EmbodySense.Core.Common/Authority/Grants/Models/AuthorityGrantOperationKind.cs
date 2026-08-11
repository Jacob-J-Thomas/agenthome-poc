namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Identifies one explicit authority-grant lifecycle operation.</summary>
public enum AuthorityGrantOperationKind
{
    /// <summary>The operation is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>Create the first immutable active revision.</summary>
    Create = 1,
    /// <summary>Create a strict-subset successor without changing exact dependency pins.</summary>
    Narrow = 2,
    /// <summary>Create a suspended successor.</summary>
    Suspend = 3,
    /// <summary>Create a freshly authorized active successor that may change exact dependency pins.</summary>
    Replace = 4,
    /// <summary>Create a terminally revoked successor.</summary>
    Revoke = 5,
    /// <summary>Create a terminally expired successor after the trusted expiry endpoint.</summary>
    Expire = 6,
}

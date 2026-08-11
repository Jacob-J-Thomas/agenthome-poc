namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Identifies the durable or fail-closed disposition of one grant operation.</summary>
public enum AuthorityGrantOperationOutcome
{
    /// <summary>The outcome is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>An immutable successor was committed.</summary>
    Committed = 1,
    /// <summary>The bounded request was invalid.</summary>
    Invalid = 2,
    /// <summary>Current server-owned authority denied the operation.</summary>
    Denied = 3,
    /// <summary>The exact target or dependency was not found.</summary>
    NotFound = 4,
    /// <summary>Optimistic state, changed intent, or exact dependency evidence conflicted.</summary>
    Conflict = 5,
    /// <summary>A finite contract or persistence quota was exhausted.</summary>
    LimitExceeded = 6,
    /// <summary>The operation could not begin safely.</summary>
    Unavailable = 7,
    /// <summary>The durable outcome could not be proved safely.</summary>
    Ambiguous = 8,
}

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies the closed outcomes of an exact contextual-role revision read.</summary>
public enum ContextualRoleRevisionReadStatus
{
    /// <summary>An undefined result that is never produced by a valid implementation.</summary>
    Unknown = 0,
    /// <summary>The exact immutable revision was found.</summary>
    Found = 1,
    /// <summary>No revision exists for the exact identity.</summary>
    NotFound = 2,
    /// <summary>The request was malformed and no read was performed.</summary>
    Invalid = 3,
    /// <summary>The persistence boundary was unavailable before a trusted read could complete.</summary>
    Unavailable = 4,
    /// <summary>The exact revision could not be proved because durable workspace evidence was inconsistent.</summary>
    Ambiguous = 5
}

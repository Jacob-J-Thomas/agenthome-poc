namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies the closed outcomes of a current contextual-role lifecycle read.</summary>
public enum ContextualRoleLifecycleReadStatus
{
    /// <summary>An undefined result that is never produced by a valid implementation.</summary>
    Unknown = 0,
    /// <summary>The proved lifecycle projection was found.</summary>
    Found = 1,
    /// <summary>No lifecycle projection exists for the exact stable role identity.</summary>
    NotFound = 2,
    /// <summary>The requested role identity was malformed.</summary>
    Invalid = 3,
    /// <summary>The persistence boundary was unavailable before a trusted read could complete.</summary>
    Unavailable = 4,
    /// <summary>The lifecycle projection could not be proved because durable evidence was inconsistent.</summary>
    Ambiguous = 5
}

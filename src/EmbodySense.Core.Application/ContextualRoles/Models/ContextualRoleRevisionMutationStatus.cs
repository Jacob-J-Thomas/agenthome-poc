namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies the closed outcomes of a contextual-role revision mutation request.</summary>
public enum ContextualRoleRevisionMutationStatus
{
    /// <summary>An undefined result that is never produced by a valid implementation.</summary>
    Unknown = 0,
    /// <summary>The immutable revision was accepted.</summary>
    Accepted = 1,
    /// <summary>The request failed structured contract validation.</summary>
    Invalid = 2,
    /// <summary>The expected predecessor did not match current durable state.</summary>
    Conflict = 3
}

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies the closed outcomes of a contextual-role catalog read.</summary>
public enum ContextualRoleCatalogReadStatus
{
    /// <summary>An undefined outcome that valid implementations never produce.</summary>
    Unknown = 0,
    /// <summary>A complete bounded page was proved, including an empty page.</summary>
    Available = 1,
    /// <summary>The cursor or page bound was malformed.</summary>
    Invalid = 2,
    /// <summary>Persistence was unavailable before a trusted page could be proved.</summary>
    Unavailable = 3,
    /// <summary>Durable role evidence was inconsistent or physically ambiguous.</summary>
    Ambiguous = 4
}

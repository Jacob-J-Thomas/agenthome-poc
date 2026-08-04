namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Identifies the closed lifecycle vocabulary for a contextual-role revision.</summary>
public enum ContextualRoleStatus
{
    /// <summary>An undefined status that is never valid for a revision.</summary>
    Unknown = 0,
    /// <summary>A revision that has not been published.</summary>
    Draft = 1,
    /// <summary>A revision intended for later admission by a separate authority boundary.</summary>
    Published = 2,
    /// <summary>A revision that is no longer eligible for new admission.</summary>
    Disabled = 3,
    /// <summary>A retained historical revision that is no longer active.</summary>
    Archived = 4,
    /// <summary>A retained historical revision superseded by another exact revision.</summary>
    Replaced = 5
}

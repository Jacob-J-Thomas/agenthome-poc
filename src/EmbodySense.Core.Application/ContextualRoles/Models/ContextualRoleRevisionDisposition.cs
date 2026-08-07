namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies one immutable revision's proved current or historical lifecycle disposition.</summary>
public enum ContextualRoleRevisionDisposition
{
    /// <summary>No disposition was proved.</summary>
    Unknown = 0,
    /// <summary>The exact revision is the role's active current revision.</summary>
    Active = 1,
    /// <summary>The exact historical revision was replaced by a later immutable revision.</summary>
    Replaced = 2,
    /// <summary>The exact revision is current but explicitly disabled.</summary>
    Disabled = 3,
    /// <summary>The exact revision remains retained beneath a permanently tombstoned role identity.</summary>
    Tombstoned = 4
}

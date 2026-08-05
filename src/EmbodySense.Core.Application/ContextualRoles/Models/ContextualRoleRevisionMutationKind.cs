namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies the closed contextual-role revision and lifecycle mutations.</summary>
public enum ContextualRoleRevisionMutationKind
{
    /// <summary>An undefined mutation that is never valid.</summary>
    Unknown = 0,
    /// <summary>Creates the first immutable role revision and active lifecycle projection.</summary>
    Create = 1,
    /// <summary>Appends an immutable replacement revision without rewriting its predecessor.</summary>
    Replace = 2,
    /// <summary>Disables the current immutable revision for later admission.</summary>
    Disable = 3,
    /// <summary>Re-enables the same current immutable revision after explicit disablement.</summary>
    Reenable = 4,
    /// <summary>Permanently tombstones the role while retaining revision and lifecycle history.</summary>
    Tombstone = 5
}

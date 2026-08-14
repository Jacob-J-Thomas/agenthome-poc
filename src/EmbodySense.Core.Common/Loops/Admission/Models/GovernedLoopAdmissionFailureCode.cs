namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Identifies one stable, value-free governed-loop admission failure classification.</summary>
public enum GovernedLoopAdmissionFailureCode
{
    /// <summary>No failure occurred.</summary>
    None = 0,

    /// <summary>The pinned contextual-role revision did not match the graph or grant.</summary>
    RoleMismatch = 1,

    /// <summary>The pinned contextual role was missing.</summary>
    RoleNotFound = 2,

    /// <summary>The pinned contextual role was not currently published and active.</summary>
    RoleInactive = 3,

    /// <summary>The pinned contextual role had been replaced or tombstoned.</summary>
    RoleReplaced = 4,

    /// <summary>The contextual role did not apply to the canonical workspace.</summary>
    RoleWorkspaceMismatch = 5,

    /// <summary>The contextual-role instruction source could not be proved exactly.</summary>
    RoleSourceMismatch = 6,

    /// <summary>The exact authority grant was missing or substituted.</summary>
    GrantMismatch = 7,

    /// <summary>The exact authority grant was not currently effective.</summary>
    GrantInactive = 8,

    /// <summary>The effective authority intersection denied admission.</summary>
    AuthorityDenied = 11,

    /// <summary>The exact requirements are structurally or policy-incompatible with the already-proved non-widening admitted ceiling.</summary>
    /// <remarks>Current catalog, provider, host, store, or evidence unavailability is nonterminal and cannot produce this rejection.</remarks>
    CapabilityResolutionDenied = 12
}

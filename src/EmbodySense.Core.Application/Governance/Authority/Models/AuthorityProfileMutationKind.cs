namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>Identifies one explicit persisted authority-profile lifecycle operation.</summary>
public enum AuthorityProfileMutationKind
{
    /// <summary>Creates the first immutable profile revision.</summary>
    Create = 1,
    /// <summary>Appends a caller-supplied successor profile revision.</summary>
    Revise = 2,
    /// <summary>Appends a successor revision with a changed declared status only.</summary>
    TransitionStatus = 3,
    /// <summary>Retains an irreversible lifecycle tombstone without rewriting history.</summary>
    Tombstone = 4
}

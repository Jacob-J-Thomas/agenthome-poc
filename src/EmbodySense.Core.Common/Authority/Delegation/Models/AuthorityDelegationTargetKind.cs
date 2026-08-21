namespace EmbodySense.Core.Common.Authority.Delegation.Models;

/// <summary>Identifies the exact kind of immutable delegation target.</summary>
public enum AuthorityDelegationTargetKind
{
    /// <summary>No supported target kind was supplied.</summary>
    Unknown = 0,
    /// <summary>The target is one exact contextual-role revision.</summary>
    Role = 1,
    /// <summary>The target is one exact published loop revision under an exact role.</summary>
    Loop = 2,
    /// <summary>The target is one exact node in one exact published loop revision under an exact role.</summary>
    Node = 3,
}

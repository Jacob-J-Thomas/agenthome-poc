namespace EmbodySense.Core.Common.Loops.Execution.Authority.Models;

/// <summary>Identifies the closed result of resolving one exact authority grant and its bound dependencies.</summary>
public enum GovernedLoopEffectAuthorityGrantPosture
{
    /// <summary>The exact grant and all bound dependencies remain eligible for effect evaluation.</summary>
    Active = 1,

    /// <summary>The grant has not reached its trusted effective time.</summary>
    NotEffective = 2,

    /// <summary>The exact grant is suspended.</summary>
    Suspended = 3,

    /// <summary>The exact grant is revoked.</summary>
    Revoked = 4,

    /// <summary>The exact grant is expired by lifecycle posture or trusted time.</summary>
    Expired = 5,

    /// <summary>The requested grant revision is stale relative to the resolved current revision.</summary>
    Stale = 6,

    /// <summary>The exact bound authority profile could not be resolved.</summary>
    ProfileUnavailable = 7,

    /// <summary>The exact bound contextual role could not be resolved.</summary>
    RoleUnavailable = 8,

    /// <summary>The exact bound published loop revision could not be resolved.</summary>
    LoopUnavailable = 9,

    /// <summary>The grant request exceeds one or more exact profile, role, or loop ceilings.</summary>
    CeilingExceeded = 10,

    /// <summary>The grant's first exact bound-run completion claim has been durably consumed.</summary>
    Completed = 11
}

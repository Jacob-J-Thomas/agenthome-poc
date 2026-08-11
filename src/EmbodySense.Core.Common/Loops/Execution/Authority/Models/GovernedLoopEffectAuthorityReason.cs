namespace EmbodySense.Core.Common.Loops.Execution.Authority.Models;

/// <summary>Classifies why one exact effect-authority disposition was reached.</summary>
public enum GovernedLoopEffectAuthorityReason
{
    /// <summary>All current proof is exact and no admitted authority dimension was narrowed.</summary>
    ActiveExact = 1,

    /// <summary>Current proof remains sufficient after one or more authority dimensions were narrowed.</summary>
    ActiveNarrowed = 2,

    /// <summary>The exact current grant is not effective at trusted evaluation time.</summary>
    GrantNotEffective = 3,

    /// <summary>The exact current grant is suspended.</summary>
    GrantSuspended = 4,

    /// <summary>The exact current grant is revoked.</summary>
    GrantRevoked = 5,

    /// <summary>The exact current grant is expired by lifecycle posture or trusted time.</summary>
    GrantExpired = 6,

    /// <summary>The resolved grant does not equal the exact admitted grant revision and hash.</summary>
    GrantStale = 7,

    /// <summary>No current grant exists for the exact admitted identity.</summary>
    GrantMissing = 8,

    /// <summary>Current grant evidence was malformed or failed integrity validation.</summary>
    GrantInvalid = 9,

    /// <summary>Current grant evidence could not be read conclusively.</summary>
    GrantUnavailable = 10,

    /// <summary>Current grant resolution produced more than one plausible result.</summary>
    GrantAmbiguous = 11,

    /// <summary>The current dependency evidence does not equal the admitted dependency evidence.</summary>
    DependencyMismatch = 12,

    /// <summary>An exact capability required by this effect is no longer active.</summary>
    CapabilityInactive = 13,

    /// <summary>A capability with the required stable identity resolved to different immutable evidence.</summary>
    CapabilityDrifted = 14,

    /// <summary>Current capability evidence could not be read conclusively.</summary>
    CapabilityUnavailable = 15,

    /// <summary>Current capability resolution produced more than one plausible result.</summary>
    CapabilityAmbiguous = 16,

    /// <summary>The current profile, role, or loop binding differs from the admitted binding.</summary>
    BindingMismatch = 17,

    /// <summary>The exact required capability pins are outside the current effective ceiling.</summary>
    EffectOutsideCeiling = 18,

    /// <summary>The boundary request itself was malformed or inconsistent.</summary>
    InvalidRequest = 19,

    /// <summary>The exact bound authority profile could not be resolved.</summary>
    ProfileUnavailable = 20,

    /// <summary>The exact bound contextual role could not be resolved.</summary>
    RoleUnavailable = 21,

    /// <summary>The exact bound published loop revision could not be resolved.</summary>
    LoopUnavailable = 22,

    /// <summary>The grant exceeds an exact profile, role, or loop ceiling.</summary>
    CeilingExceeded = 23,

    /// <summary>The authority decision could not be durably appended because its evidence store was unavailable.</summary>
    EvidenceUnavailable = 24,

    /// <summary>The authority decision append produced an ambiguous durable outcome.</summary>
    EvidenceAmbiguous = 25,

    /// <summary>The authority decision append encountered an optimistic concurrency conflict.</summary>
    EvidenceConflict = 26
}

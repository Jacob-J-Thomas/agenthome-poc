namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Identifies one exact requested-authority subset violation.</summary>
public enum AuthorityCeilingSubsetViolationCode
{
    /// <summary>The violation is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>A supplied ceiling or capability-id maximum is malformed.</summary>
    InvalidContract = 1,
    /// <summary>An exact capability identity is outside the profile ceiling.</summary>
    CapabilityIdentityOutsideProfile = 2,
    /// <summary>A capability id is outside the contextual-role maximum.</summary>
    CapabilityIdOutsideRole = 3,
    /// <summary>A capability id is outside the exact loop-binding maximum.</summary>
    CapabilityIdOutsideLoop = 4,
    /// <summary>A data class is outside the profile ceiling.</summary>
    DataClassOutsideProfile = 5,
    /// <summary>The target-count maximum exceeds the profile ceiling.</summary>
    TargetCountExceedsProfile = 6,
    /// <summary>The side-effect class exceeds the profile ceiling.</summary>
    SideEffectClassExceedsProfile = 7,
    /// <summary>Recurrence would exceed the profile ceiling.</summary>
    RecurrenceExceedsProfile = 8,
    /// <summary>External publication would exceed the profile ceiling.</summary>
    ExternalPublicationExceedsProfile = 9,
    /// <summary>Irreversible action would exceed the profile ceiling.</summary>
    IrreversibleActionExceedsProfile = 10,
}

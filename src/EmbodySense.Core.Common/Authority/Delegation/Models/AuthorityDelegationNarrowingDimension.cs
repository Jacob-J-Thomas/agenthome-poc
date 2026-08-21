namespace EmbodySense.Core.Common.Authority.Delegation.Models;

/// <summary>Identifies one authority-ceiling dimension that a delegated envelope strictly narrows.</summary>
public enum AuthorityDelegationNarrowingDimension
{
    /// <summary>The exact capability identity set is narrower.</summary>
    CapabilityIdentitySet = 1,
    /// <summary>The data-class set is narrower.</summary>
    DataClassSet = 2,
    /// <summary>The maximum target count is lower.</summary>
    TargetCount = 3,
    /// <summary>The maximum side-effect class is lower.</summary>
    SideEffectClass = 4,
    /// <summary>Recurrence is removed.</summary>
    Recurrence = 5,
    /// <summary>External publication is removed.</summary>
    ExternalPublication = 6,
    /// <summary>Irreversible action is removed.</summary>
    IrreversibleAction = 7,
}

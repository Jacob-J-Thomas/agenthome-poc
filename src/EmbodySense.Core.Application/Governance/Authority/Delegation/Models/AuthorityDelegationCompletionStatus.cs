namespace EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

/// <summary>Classifies exact parent and target completion posture.</summary>
public enum AuthorityDelegationCompletionStatus
{
    /// <summary>No supported posture was supplied.</summary>
    Unknown = 0,
    /// <summary>Neither exact completion boundary has completed.</summary>
    Active = 1,
    /// <summary>The exact parent execution completed.</summary>
    ParentCompleted = 2,
    /// <summary>The exact target completed.</summary>
    TargetCompleted = 3,
    /// <summary>Completion truth is unavailable.</summary>
    Unavailable = 4,
    /// <summary>Completion evidence admits multiple postures.</summary>
    Ambiguous = 5,
    /// <summary>Completion sources conflict.</summary>
    Conflict = 6,
}

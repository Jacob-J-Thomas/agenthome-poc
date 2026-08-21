namespace EmbodySense.Core.Common.Authority.Delegation.Models;

/// <summary>Defines the local completion boundary of one delegated-authority envelope.</summary>
public enum AuthorityDelegationCompletionConstraintKind
{
    /// <summary>No supported completion constraint was supplied.</summary>
    Unknown = 0,
    /// <summary>No target-completion constraint applies; a finite expiry is required.</summary>
    None = 1,
    /// <summary>The exact target completion state terminates the envelope.</summary>
    TargetCompletion = 2,
}

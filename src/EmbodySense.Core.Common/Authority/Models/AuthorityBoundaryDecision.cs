namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Identifies the exact non-executing authority boundary decision.
/// </summary>
public enum AuthorityBoundaryDecision
{
    /// <summary>The decision is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The contract has no boundary requiring review, pause, or denial.</summary>
    Direct = 1,
    /// <summary>A human or configured review gate is required before any future execution.</summary>
    Review = 2,
    /// <summary>Evaluation must pause and escalate without executing an effect.</summary>
    Pause = 3,
    /// <summary>The requested authority is denied.</summary>
    Deny = 4
}

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Identifies whether one validated canonical frontier has deterministic work or terminal posture.</summary>
public enum GovernedLoopSequentialFrontierSelectionStatus
{
    /// <summary>The frontier or admitted plan is invalid.</summary>
    Invalid = 0,

    /// <summary>One exact node is ready for a new attempt.</summary>
    Ready,

    /// <summary>One exact node was already committed Running and requires evidence-only reconciliation.</summary>
    Running,

    /// <summary>The frontier is durably blocked on review and cannot dispatch.</summary>
    ReviewBlocked,

    /// <summary>The frontier is terminal and cannot dispatch.</summary>
    Terminal
}

namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

/// <summary>Classifies the caller action required after one success-exit completion attempt.</summary>
public enum GovernedLoopFirstBoundRunCompletionDisposition
{
    /// <summary>The terminal callback and any required grant-completion evidence are durable.</summary>
    Completed = 1,

    /// <summary>The same completion was already durable; the caller must reload and authenticate that exact completed run.</summary>
    AlreadyCompleted = 2,

    /// <summary>The callback ran, but durable completion evidence is unconfirmed; retain truthful terminal state with an integrity warning.</summary>
    NeedsReview = 3,

    /// <summary>The callback did not run because completion authority was rejected or unavailable.</summary>
    Rejected = 4
}

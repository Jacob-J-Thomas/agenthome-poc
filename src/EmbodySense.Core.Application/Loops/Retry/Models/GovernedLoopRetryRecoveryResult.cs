namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Reports bounded retry-schedule recovery without hiding invalid retained candidates.</summary>
/// <param name="Recovered">The number of exact missing checkpoints recovered.</param>
/// <param name="NeedsReview">The number of candidates that were corrupt, conflicting, or unavailable.</param>
public sealed record GovernedLoopRetryRecoveryResult(int Recovered, int NeedsReview);

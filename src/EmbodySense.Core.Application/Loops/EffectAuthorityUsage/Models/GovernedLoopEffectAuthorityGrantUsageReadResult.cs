namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

/// <summary>Returns the authenticated completion-consumption posture of one exact authority grant.</summary>
/// <param name="Status">The closed status of the exact immutable grant's completion evidence.</param>
public sealed record GovernedLoopEffectAuthorityGrantUsageReadResult(GovernedLoopEffectAuthorityGrantUsageReadStatus Status);

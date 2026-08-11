namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

/// <summary>Returns the closed durable posture of one authority-usage request.</summary>
/// <param name="Status">The validated terminal usage status.</param>
public sealed record GovernedLoopEffectAuthorityUsageStoreResult(GovernedLoopEffectAuthorityUsageStoreStatus Status);

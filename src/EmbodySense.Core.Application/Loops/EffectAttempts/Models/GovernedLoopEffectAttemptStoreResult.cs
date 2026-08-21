using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.EffectAttempts.Models;

/// <summary>Returns one closed effect-attempt store outcome, its durable head, and optional execution ownership.</summary>
public sealed record GovernedLoopEffectAttemptStoreResult(
    GovernedLoopEffectAttemptStoreStatus Status,
    GovernedLoopEffectAttempt? Attempt = null,
    IGovernedLoopEffectAttemptLease? Lease = null);

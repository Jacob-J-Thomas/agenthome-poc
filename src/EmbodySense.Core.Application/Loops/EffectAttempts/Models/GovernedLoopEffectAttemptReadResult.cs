using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.EffectAttempts.Models;

/// <summary>Returns one detached read-only effect-attempt observation.</summary>
/// <param name="Status">The closed current-head read posture.</param>
/// <param name="Attempt">The detached canonical current head only when <paramref name="Status"/> is <see cref="GovernedLoopEffectAttemptReadStatus.Current"/>.</param>
public sealed record GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus Status, GovernedLoopEffectAttempt? Attempt = null);

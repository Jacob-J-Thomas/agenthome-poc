namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Binds a pre-dispatch review to one immutable effect attempt whose irreversible boundary is conclusively un-crossed.</summary>
/// <param name="EffectAttemptId">The stable effect-attempt identity.</param>
/// <param name="OperationId">The exact idempotency or actuator operation identity.</param>
/// <param name="EffectGeneration">The positive exact effect generation.</param>
/// <param name="IntentHash">The exact immutable prepared-effect intent hash.</param>
/// <param name="PreparationHash">The exact pre-dispatch preparation evidence hash.</param>
/// <param name="DispatchCertainty">The retained certainty posture; schema 1 accepts only <see cref="HumanReviewEffectDispatchCertainty.NotDispatched"/>.</param>
/// <param name="EffectAttemptHash">The canonical hash of every prior effect-attempt binding field.</param>
public sealed record HumanReviewEffectAttemptBinding(string EffectAttemptId, string OperationId, long EffectGeneration, string IntentHash, string PreparationHash, HumanReviewEffectDispatchCertainty DispatchCertainty, string EffectAttemptHash);

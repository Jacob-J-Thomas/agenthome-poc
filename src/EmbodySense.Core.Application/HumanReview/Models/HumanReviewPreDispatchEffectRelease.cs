using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Supplies the retained canonical Human Review proof that authorizes one previously prepared effect to cross its dispatch boundary.</summary>
/// <param name="Request">The immutable pre-dispatch Human Review request bound to the exact Action attempt.</param>
/// <param name="ReleaseReceipt">The durable release receipt whose effect snapshot was reread before the whole-run release compare-exchange.</param>
/// <remarks>This proof is assembled only from durable run state after a conclusive release. It does not grant a new authority ceiling or permit an effect whose identity, preparation, or retained request differs.</remarks>
public sealed record HumanReviewPreDispatchEffectRelease(HumanReviewRequest Request, HumanReviewContinuationReleaseReceipt ReleaseReceipt);

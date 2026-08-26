using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Names the exact reviewed effect attempt whose current server-derived identity and preparation evidence must be re-read.</summary>
/// <param name="Binding">The immutable Human Review binding that pins run, frontier, revision, target, precondition, payload, and reviewed effect hashes.</param>
/// <param name="EffectAttempt">The exact reviewed pre-dispatch effect-attempt reference.</param>
public sealed record HumanReviewCurrentEffectAttemptEvidenceQuery(HumanReviewBinding Binding, HumanReviewEffectAttemptBinding EffectAttempt);

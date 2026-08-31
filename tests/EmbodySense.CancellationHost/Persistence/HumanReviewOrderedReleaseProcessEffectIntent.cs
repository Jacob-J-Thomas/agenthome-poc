using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed record HumanReviewOrderedReleaseProcessEffectIntent(
    HumanReviewContinuationActionIntent Action,
    HumanReviewContinuationCompletionIntent Completion,
    DateTimeOffset ReleaseAtUtc);

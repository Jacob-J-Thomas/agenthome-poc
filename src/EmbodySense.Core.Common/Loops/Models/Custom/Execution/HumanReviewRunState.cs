using System.Collections.Immutable;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>Persists one immutable Human Review request, its initial lifecycle head, and append-only admission evidence inside the canonical run artifact.</summary>
/// <param name="Request">The exact immutable request bound to the parked frontier.</param>
/// <param name="Lifecycle">The initial pending lifecycle head.</param>
/// <param name="Evidence">The ordered append-only review evidence chain.</param>
public sealed record HumanReviewRunState(
    [property: JsonRequired] HumanReviewRequest Request,
    [property: JsonRequired] HumanReviewLifecycle Lifecycle,
    [property: JsonRequired] ImmutableArray<HumanReviewEvidence> Evidence);
